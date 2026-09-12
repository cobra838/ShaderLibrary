using ShaderLibrary.Common;
using ShaderLibrary.WiiU;
using static ShaderLibrary.CompileTool.Gx2PixelShaderConversion;

namespace ShaderLibrary.CompileTool;

/// <summary>Translate a GX2 vertex program with the interface declared by its GFD.</summary>
/// <remarks>The target vertex buffers must supply the declared logical attributes.
/// This does not reconstruct the game's fetch format or adapt an effect's buffer layout.</remarks>
public static class Gx2VertexShaderConversion
{
    public sealed record VertexAttribute(string Name, int Location, GX2ShaderVarType Type, int Register);
    public sealed record StreamOutputElement(int ByteOffset, int Location, int Component)
    {
        // Maxwell transform-feedback tables select DWORDs in the output attribute space.
        public byte NvnVaryingLocation => checked((byte)(32 + Location * 4 + Component));
    }
    public sealed record StreamOutputBuffer(int Buffer, uint Stride, StreamOutputElement[] Elements);
    public sealed record PreparedShader(string Code, VertexAttribute[] InputAttributes,
        int[] OutputSemantics, UniformBinding[] UniformBuffers, TextureBinding[] Textures)
    {
        public StreamOutputBuffer[] StreamOutputs { get; init; } = Array.Empty<StreamOutputBuffer>();
    }

    /// <summary>Compiled vertex code and the stream layout required by the target pipeline.</summary>
    public sealed class CompiledShader : UAMShaderCompiler.ShaderOutput
    {
        public StreamOutputBuffer[] StreamOutputs { get; init; } = Array.Empty<StreamOutputBuffer>();
    }

    public static PreparedShader Prepare(GSHFile.GX2Shader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.VertexHeader == null || source.VertexData == null)
            throw new ArgumentException("A GX2 vertex header and program are required.", nameof(source));
        var translator = new Gx2ShaderTranslator(source.VertexHeader, source.VertexData);
        var result = translator.Run();
        return new(result.Code, translator.VertexInputs.ToArray(), translator.VertexOutputs.ToArray(),
            result.UniformBuffers, result.Textures) { StreamOutputs = translator.StreamOutputs.ToArray() };
    }

    public static CompiledShader Compile(GSHFile.GX2Shader source, string? compilerPath = null)
    {
        var prepared = Prepare(source);
        var compiled = UAMShaderCompiler.CompileByText(prepared.Code, "vert", compilerPath);
        return new() { ShaderCode = compiled.ShaderCode, Control = compiled.Control, StreamOutputs = prepared.StreamOutputs };
    }
}
