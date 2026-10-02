# 重建补丁 / Rebuilding the patch

## 中文

环境：Windows，.NET Framework 4.x 自带的 csc（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，C# 5 语法），[dnlib](https://www.nuget.org/packages/dnlib) 4.4.0 DLL。

```
:: 1. 编译 hook（用你自己的强名密钥）
sn -k mykey.snk
csc /nologo /target:library /keyfile:mykey.snk /optimize+ /out:HookTemplate.dll HookTemplate.cs

:: 2. 提取公钥供 Patcher 使用
::    Patcher.cs 会读取 hook.pub（完整公钥 blob，160 字节）写入 AssemblyRef。
::    提取方式（PowerShell）：
$an = [Reflection.AssemblyName]::GetAssemblyName('HookTemplate.dll')
[IO.File]::WriteAllBytes('hook.pub', $an.GetPublicKey())

:: 3. 打补丁（对原始未修改的 Newtonsoft.Json.dll 13.0.0.x）
csc /nologo /out:Patcher.exe /r:dnlib.dll /r:System.Numerics.dll Patcher.cs
Patcher.exe Newtonsoft.Json.dll.orig Newtonsoft.Json.patched.dll
```

注意：
- Patcher 会把程序集版本改为 13.0.0.1 以失效 NGEN 缓存；`Lyricify.exe.config` 需有对应 bindingRedirect
- hook 必须强名签名 + `[assembly: SecurityTransparent]`（Newtonsoft 是 APTCA/Level2 程序集）
- MemberRef 的参数类型必须与 hook 方法签名逐字一致（如 `IFormatProvider` 不能写成 `CultureInfo`），否则运行时 MissingMethodException / VerificationException

## English

Environment: Windows, the csc shipped with .NET Framework 4.x (C# 5 syntax is enough), dnlib 4.4.0.

Steps: compile the hook with your own strong-name key, extract the public key blob to `hook.pub` (read by Patcher.cs), then run Patcher against a pristine Newtonsoft.Json.dll 13.0.0.x. See the Chinese notes for pitfalls (NGEN cache vs assembly version, SecurityTransparent requirement, exact member-ref signatures).
