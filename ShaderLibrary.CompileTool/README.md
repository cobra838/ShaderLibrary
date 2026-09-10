# Credits

UAM for shader compiling tools
Compiler source: https://github.com/nvnprogram/uam-nvn (master), with local NVN output fixes.
The bundled Windows executable includes the MinGW runtime libraries statically.

`UAMShaderCompiler.CompileByText(text, kind)` compiles GLSL without an existing BNSH.
It uses `uam.exe` next to the CompileTool assembly; an explicit compiler path may also be supplied.
Compilation errors propagate to the caller. Each invocation uses a separate temporary directory.

This compiler produces Switch shader binaries. It does not perform Wii U shader translation
or establish the buffer bindings required by a particular game.

Ryujinx for shader decompiling tools
Source : https://github.com/Ryujinx/Ryujinx
