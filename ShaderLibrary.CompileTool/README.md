# Credits

UAM for shader compiling tools
Compiler source: https://github.com/nvnprogram/uam-nvn (master), with local NVN output fixes.
The bundled Windows executable includes the MinGW runtime libraries statically.

`UAMShaderCompiler.CompileByText(text, kind)` compiles GLSL without an existing BNSH.
It uses `uam.exe` next to the CompileTool assembly; an explicit compiler path may also be supplied.
Compilation errors propagate to the caller. Each invocation uses a separate temporary directory.

This compiler produces Switch shader binaries. It does not perform Wii U shader translation
or establish the buffer bindings required by a particular game.

## NVN compute recompilation

`ComputeShaderConversion.Prepare(sourceStage)` returns GLSL and its driver uniform
binding. It reconstructs UBO/SSBO bindings from the source machine code, preserves
driver-table reads through c[0], and embeds c[1] constants as unsigned bit patterns.
`ComputeShaderConversion.Compile(sourceStage)` prepares and compiles in one call.
Both operate on the supplied shader without stock files.

To edit the GLSL before compiling:

```csharp
var prepared = ComputeShaderConversion.Prepare(sourceStage);
var edited = prepared with { Code = editedGlsl };
var result = ComputeShaderConversion.Compile(edited);
```

The result contains code and control bytes for a BNSH compute stage. BNSH reflection
must still describe the compiled shader's interface. Recompilation is currently
NVN to GLSL to NVN, not Wii U to Switch translation.

Ryujinx for shader decompiling tools
Source : https://github.com/Ryujinx/Ryujinx
