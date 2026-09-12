// SPDX-License-Identifier: MPL-2.0
// Latte instruction decoding and register semantics adapted from Cemu:
// src/Cafe/HW/Latte/LegacyShaderDecompiler/LatteDecompiler.cpp and
// src/Cafe/HW/Latte/Core/LatteShader.cpp. See Libs/Cemu License.txt.
using ShaderLibrary.Common;
using ShaderLibrary.WiiU;

namespace ShaderLibrary.CompileTool;

/// <summary>Translate a GX2 pixel program using its own GFD metadata.</summary>
/// <remarks>
/// Supports ALU, texture programs and structured IF blocks. Unsupported instructions throw;
/// no original Switch program or replacement shader is used. Locations are GX2
/// semantics/bindings, so the other stages must be converted with the same mapping.
/// This does not construct an effect BNSH or change the effect's uniform layout.
/// </remarks>
public static class Gx2PixelShaderConversion
{
    public sealed record UniformBinding(string Name, uint Binding, uint Size);
    public sealed record TextureBinding(string Name, uint Binding, GX2SamplerVarType Type);
    public sealed record PreparedShader(string Code, int[] InputSemantics,
        int[] ColorOutputs, UniformBinding[] UniformBuffers, TextureBinding[] Textures);

    public static UAMShaderCompiler.ShaderOutput Compile(GSHFile.GX2Shader source,
        string? compilerPath = null) =>
        UAMShaderCompiler.CompileByText(Prepare(source).Code, "frag", compilerPath);

    public static PreparedShader Prepare(GSHFile.GX2Shader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelHeader == null || source.PixelData == null)
            throw new ArgumentException("A GX2 pixel header and program are required.", nameof(source));
        return new Gx2ShaderTranslator(source.PixelHeader, source.PixelData).Run();
    }

}
