# Credits

UAM for shader compiling tools
Compiler source: https://github.com/nvnprogram/uam-nvn (master), with local NVN output fixes.
The bundled Windows executable includes the MinGW runtime libraries statically.

`UAMShaderCompiler.CompileByText(text, kind)` compiles GLSL without an existing BNSH.
It uses `uam.exe` next to the CompileTool assembly; an explicit compiler path may also be supplied.
Compilation errors propagate to the caller. Each invocation uses a separate temporary directory.

The UAM wrapper compiles GLSL into Switch shader binaries. Platform-specific source
preparation and resource bindings are handled by the conversion classes below.

## GX2 vertex and pixel shaders to NVN

`Gx2PixelShaderConversion.Prepare(gx2Shader)` reads the pixel program and metadata
from a `GSHFile.GX2Shader`. It returns GLSL, GX2 input semantics, color outputs,
uniform-buffer bindings/sizes and texture bindings/types. It needs no Switch file.
`Gx2PixelShaderConversion.Compile(gx2Shader)` prepares and compiles the pixel stage.

`Gx2VertexShaderConversion.Prepare(gx2Shader)` reads the vertex program and metadata
from the same GFD representation. It returns GLSL, input attribute names/types,
locations/registers, output semantics, uniform buffers and textures.
`Gx2VertexShaderConversion.Compile(gx2Shader)` compiles the vertex stage without a
Switch template. `CALL_FS` loads the logical attributes into the registers selected
by the GX2 semantic table; position and parameter exports use the source registers.
The caller must supply vertex buffers with the corresponding format, swizzle and
instance divisor. Float3 attributes also use a vec4 shader input so the fetch
configuration, rather than this translator, supplies the fourth component.

Unconditional GX2 stream-output writes become integer transform-feedback outputs,
preserving the written DWORDs even if the source registers change later.
Both `Prepare` and `Compile` return `StreamOutputs`: buffer indices, byte strides
and element mappings (byte offset, output location/component and the Maxwell
varying index). The target pipeline must configure transform feedback using this
layout and bind the destination buffers. UAM's control bytes alone do not carry
that configuration. Stream outputs use free locations without replacing the
ordinary vertex-to-pixel outputs.

Both stages share an instruction translator. ALU groups evaluate
their inputs before committing register writes, including masked writes and PV/PS.
It supports scalar arithmetic, DOT4 groups, integer operations, nested IF/ELSE blocks,
integer predicates, ALU stack pops followed by explicit POP, 2D/3D sampling, shadow sampling
and 2D FETCH4 with its component-order conversion. Uniform-block VFETCH preserves
dynamic vec4 indexing. Literals retain their source bit patterns. Unsupported
operations fail with their bytecode offset.
`RECIP_FF` and `RECIPSQRT_FF` follow Cemu's lowering of infinite results to
signed zero. Finite values, infinities and signed zeros were checked through
NVN compilation and disassembly; behavior on Wii U hardware is not yet verified.

Current limits include CUBE, conditional/indexed/burst stream writes, other control-flow forms
and other texture operations/types. Rasterization-dependent special inputs also
require further work. Generated stages retain GX2 bindings and buffer layouts;
this is not yet a complete effect BNSH or `.sesetlist` conversion. Target effect
interfaces, vertex fetch configuration and validation in the game remain separate
work. Shadow-array explicit LOD uses the bundled UAM's extended GLSL overload.

The GX2 vertex header reader/writer preserves ring item size, the 32-bit stream
output enable flag, all four stream strides and the GX2R resource descriptor.
`StreamOutSize` remains an alias for the first stride; use `StreamOutStrides` for
all buffers.

Instruction decoding and register handling were adapted from Cemu under MPL-2.0
(`Libs/Cemu License.txt`), using revision `3310f3b8b184d64a62b89fd59088c799432badf5`:
`LatteDecompiler.cpp`, `LatteDecompilerEmitGLSL.cpp`, `LatteDecompilerInstructions.h`
and `LatteShader.cpp`; the vertex header layout was checked against `GX2_Shader.h`.
There is no runtime dependency on a Cemu checkout or process.

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
