// Patches Newtonsoft.Json.dll: Int32-overflow song ids are wrapped into int range
// and recorded (wrapped->real ring buffer) instead of throwing, at every plausible
// conversion site (text reader, base reader, EnsureType/JToken conversions).
// The hook implementation lives in HookTemplate.dll (loaded from the app dir).
using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

class Patcher
{
    const string OverflowMsg = "JSON integer {0} is too large or small for an Int32.";
    const string ConvertCall = "int32 [mscorlib]System.Convert::ToInt32(object, class [mscorlib]System.IFormatProvider)";

    static ModuleDefMD mod;
    static Importer importer;
    static ITypeDefOrRef hookTypeRef;
    static IMethod wrapFromString;
    static IMethod wrapFromLong;
    static IMethod safeToInt32Object;
    static IMethod maybeRepair;
    static IMethod boxedGet; // BoxedPrimitives::Get(int32), resolved from ParseReadNumber

    static int Main(string[] args)
    {
        string src = args[0];
        string dst = args[1];
        mod = ModuleDefMD.Load(src);
        importer = new Importer(mod);

        // external hook assembly reference
        var asmRef = new AssemblyRefUser("HookTemplate");
        asmRef.Version = new System.Version(1, 0, 0, 0);
        var pubBytes = System.IO.File.ReadAllBytes("hook.pub");
        Console.WriteLine("I: hook.pub bytes = " + pubBytes.Length);
        asmRef.PublicKeyOrToken = new PublicKey(pubBytes);
        asmRef.Attributes |= AssemblyAttributes.PublicKey;
        mod.UpdateRowId(asmRef);
        hookTypeRef = new TypeRefUser(mod, "", "LyricifyOverflowHook", asmRef);
        wrapFromString = new MemberRefUser(mod, "WrapFromString",
            MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.String), hookTypeRef);
        wrapFromLong = new MemberRefUser(mod, "WrapFromLong",
            MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.Int64), hookTypeRef);
        maybeRepair = new MemberRefUser(mod, "MaybeRepairLyricResponse",
            MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String), hookTypeRef);
        safeToInt32Object = new MemberRefUser(mod, "SafeToInt32Object",
            MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.Object,
                new ClassSig(importer.Import(typeof(System.IFormatProvider)))), hookTypeRef);

        PatchParseReadNumber();
        PatchBaseReadAsInt32();
        PatchConvertOrCast();
        PatchJTokenOpExplicit();
        PatchDeserializeObject();

        mod.Assembly.Version = new System.Version(13, 0, 0, 1);
        mod.Write(dst);
        Console.WriteLine("I: written " + dst);
        return 0;
    }

    static void PatchParseReadNumber()
    {
        var readerType = mod.Find("Newtonsoft.Json.JsonTextReader", false);
        var parseMethod = readerType.FindMethod("ParseReadNumber");
        var instrs = parseMethod.Body.Instructions;

        int iStr = -1;
        for (int i = 1; i < instrs.Count; i++)
            if (instrs[i].OpCode == OpCodes.Ldstr && (string)instrs[i].Operand == OverflowMsg) { iStr = i; break; }
        if (iStr < 1) { Console.WriteLine("E: ParseReadNumber ldstr not found"); Environment.Exit(1); }
        if (instrs[iStr - 1].OpCode != OpCodes.Ldarg_0) { Console.WriteLine("E: ParseReadNumber shape"); Environment.Exit(1); }
        if (instrs[iStr + 3].OpCode != OpCodes.Ldflda || instrs[iStr + 4].OpCode != OpCodes.Constrained
            || instrs[iStr + 5].OpCode != OpCodes.Callvirt)
        { Console.WriteLine("E: ParseReadNumber stringref shape"); Environment.Exit(1); }
        int iThrow = -1;
        for (int i = iStr; i < instrs.Count; i++)
            if (instrs[i].OpCode == OpCodes.Throw) { iThrow = i; break; }

        Instruction getInstr = null, stlocInstr = null, joinTarget = null;
        for (int i = 0; i < instrs.Count - 2; i++)
        {
            var call = instrs[i];
            if (call.OpCode != OpCodes.Call || !(call.Operand is IMethod)) continue;
            var mm = (IMethod)call.Operand;
            if (!mm.FullName.Contains("BoxedPrimitives::Get")) continue;
            if (mm.MethodSig.Params.Count != 1 || mm.MethodSig.Params[0].ElementType != ElementType.I4) continue;
            if (!instrs[i + 1].OpCode.Code.ToString().StartsWith("Stloc")) continue;
            var br = instrs[i + 2];
            if (br.OpCode.Code != Code.Br_S && br.OpCode.Code != Code.Br) continue;
            getInstr = call; stlocInstr = instrs[i + 1]; joinTarget = (Instruction)br.Operand;
            break;
        }
        if (getInstr == null) { Console.WriteLine("E: ParseReadNumber epilogue"); Environment.Exit(1); }
        boxedGet = (IMethod)getInstr.Operand;

        var replace = new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, (IField)instrs[iStr + 3].Operand),
            Instruction.Create(OpCodes.Constrained, (ITypeDefOrRef)instrs[iStr + 4].Operand),
            Instruction.Create(OpCodes.Callvirt, (IMethod)instrs[iStr + 5].Operand),
            Instruction.Create(OpCodes.Call, wrapFromString),
            Instruction.Create(OpCodes.Call, boxedGet),
            stlocInstr.Operand == null ? Instruction.Create(stlocInstr.OpCode) : Instruction.Create(stlocInstr.OpCode, (Local)stlocInstr.Operand),
            Instruction.Create(OpCodes.Br, joinTarget),
        };
        int start = iStr - 1;
        int removed = iThrow - start + 1;
        for (int i = 0; i < removed; i++) instrs.RemoveAt(start);
        for (int i = 0; i < replace.Length; i++) instrs.Insert(start + i, replace[i]);
        Console.WriteLine("I: ParseReadNumber patched");
    }

    static void PatchBaseReadAsInt32()
    {
        var readerType = mod.Find("Newtonsoft.Json.JsonReader", false);
        var m = readerType.FindMethod("ReadAsInt32");
        var instrs = m.Body.Instructions;
        int patched = 0;
        for (int i = 0; i < instrs.Count; i++)
        {
            var call = instrs[i];
            if (call.OpCode != OpCodes.Call || !(call.Operand is IMethod)) continue;
            var cm = (IMethod)call.Operand;
            if (cm.Name != "ToInt32" || cm.DeclaringType == null || cm.DeclaringType.FullName != "System.Convert") continue;
            if (cm.MethodSig.Params.Count != 2) continue;
            call.Operand = safeToInt32Object;
            patched++;
        }
        if (patched != 1) { Console.WriteLine("E: base ReadAsInt32 Convert sites = " + patched); Environment.Exit(1); }
        Console.WriteLine("I: base ReadAsInt32 patched");
    }

    static void PatchConvertOrCast()
    {
        var cu = mod.Find("Newtonsoft.Json.Utilities.ConvertUtils", false);
        var orig = cu.FindMethod("ConvertOrCast");
        if (orig == null || orig.Body == null) { Console.WriteLine("E: ConvertOrCast not found"); Environment.Exit(1); }
        orig.Name = "ConvertOrCastOrig";

        var wrapper = new MethodDefUser("ConvertOrCast",
            MethodSig.CreateStatic(mod.CorLibTypes.Object, mod.CorLibTypes.Object,
                new ClassSig(importer.Import(typeof(System.Globalization.CultureInfo))), mod.CorLibTypes.Object),
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        wrapper.Body = new CilBody();
        var l = new Local(mod.CorLibTypes.Int64);
        wrapper.Body.Variables.Add(l);
        var body = wrapper.Body.Instructions;

        var int32Ref = importer.Import(typeof(int));
        var getTypeFromHandle = importer.Import(typeof(Type).GetMethod("GetTypeFromHandle"));
        var typeOpEq = importer.Import(typeof(Type).GetMethod("op_Equality", new[] { typeof(Type), typeof(Type) }));

        var origStart = Instruction.Create(OpCodes.Ldarg_0);
        var wrapStart = Instruction.Create(OpCodes.Ldloc, l);

        body.Add(Instruction.Create(OpCodes.Ldarg_2));
        body.Add(Instruction.Create(OpCodes.Ldtoken, int32Ref));
        body.Add(Instruction.Create(OpCodes.Call, getTypeFromHandle));
        body.Add(Instruction.Create(OpCodes.Call, typeOpEq));
        var notInt = Instruction.Create(OpCodes.Brfalse_S, origStart);
        body.Add(notInt);

        body.Add(Instruction.Create(OpCodes.Ldarg_0));
        body.Add(Instruction.Create(OpCodes.Isinst, importer.Import(typeof(long))));
        var notLong = Instruction.Create(OpCodes.Brfalse_S, origStart);
        body.Add(notLong);
        body.Add(Instruction.Create(OpCodes.Unbox_Any, importer.Import(typeof(long))));
        body.Add(Instruction.Create(OpCodes.Stloc, l));

        body.Add(Instruction.Create(OpCodes.Ldloc, l));
        body.Add(Instruction.Create(OpCodes.Ldc_I8, (long)int.MaxValue));
        var overHigh = Instruction.Create(OpCodes.Bgt_S, wrapStart);
        body.Add(overHigh);
        body.Add(Instruction.Create(OpCodes.Ldloc, l));
        body.Add(Instruction.Create(OpCodes.Ldc_I8, (long)int.MinValue));
        var overLow = Instruction.Create(OpCodes.Blt_S, wrapStart);
        body.Add(overLow);
        body.Add(Instruction.Create(OpCodes.Br_S, origStart));

        body.Add(wrapStart);
        body.Add(Instruction.Create(OpCodes.Call, wrapFromLong));
        body.Add(Instruction.Create(OpCodes.Call, boxedGet));
        body.Add(Instruction.Create(OpCodes.Ret));

        body.Add(origStart);
        body.Add(Instruction.Create(OpCodes.Ldarg_1));
        body.Add(Instruction.Create(OpCodes.Ldarg_2));
        body.Add(Instruction.Create(OpCodes.Call, importer.Import(orig)));
        body.Add(Instruction.Create(OpCodes.Ret));

        wrapper.Body.KeepOldMaxStack = true;
        wrapper.Body.MaxStack = 8;
        cu.Methods.Add(wrapper);
        Console.WriteLine("I: ConvertOrCast wrapped");
    }

    static void PatchDeserializeObject()
    {
        var jc = mod.Find("Newtonsoft.Json.JsonConvert", false);
        if (jc == null) { Console.WriteLine("E: JsonConvert not found"); Environment.Exit(1); }
        foreach (var m in jc.Methods)
        {
            if (m.Name != "DeserializeObject" || m.Body == null) continue;
            var sig = m.MethodSig;
            if (sig == null || sig.Params.Count != 3) continue;
            if (sig.Params[0].ElementType != ElementType.String) continue;
            if (sig.Params[2].FullName != "Newtonsoft.Json.JsonSerializerSettings") continue;
            var instrs = m.Body.Instructions;
            instrs.Insert(0, Instruction.Create(OpCodes.Ldarg_0));
            instrs.Insert(1, Instruction.Create(OpCodes.Call, maybeRepair));
            instrs.Insert(2, Instruction.Create(OpCodes.Starg_S, (Parameter)m.Parameters[0]));
            Console.WriteLine("I: DeserializeObject(string,Type,settings) patched");
            return;
        }
        Console.WriteLine("E: DeserializeObject 3-arg not found");
        Environment.Exit(1);
    }

    static void PatchJTokenOpExplicit()
    {
        var jt = mod.Find("Newtonsoft.Json.Linq.JToken", false);
        if (jt == null) { Console.WriteLine("E: JToken not found"); Environment.Exit(1); }
        int patched = 0;
        foreach (var m in jt.Methods)
        {
            if (m.Name != "op_Explicit" || m.Body == null) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Call || !(ins.Operand is IMethod)) continue;
                var cm = (IMethod)ins.Operand;
                if (cm.Name != "ToInt32" || cm.DeclaringType == null || cm.DeclaringType.FullName != "System.Convert") continue;
                if (cm.MethodSig.Params.Count != 2) continue;
                ins.Operand = safeToInt32Object;
                patched++;
            }
        }
        if (patched < 1) { Console.WriteLine("E: JToken op_Explicit Convert sites = " + patched); Environment.Exit(1); }
        Console.WriteLine("I: JToken op_Explicit patched (" + patched + " sites)");
    }
}
