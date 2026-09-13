using ShaderLibrary.WiiU;

namespace ShaderLibrary.CompileTool;

/// <summary>Compile a vertex/pixel pair with a shared, compact varying interface.</summary>
/// <remarks>The caller selects the source stages belonging to the same program.
/// No Switch template is used. Vertex fetch and uniform buffer layouts remain
/// those of the source; this does not construct the surrounding effect.</remarks>
public static class Gx2ShaderProgramConversion
{
    public sealed record VaryingBinding(int SourceSemantic, int Location, int ComponentMask);
    public sealed record CompiledProgram(
        Gx2VertexShaderConversion.CompiledShader Vertex,
        UAMShaderCompiler.ShaderOutput Pixel,
        Gx2VertexShaderConversion.PreparedShader VertexSource,
        Gx2PixelShaderConversion.PreparedShader PixelSource,
        VaryingBinding[] Varyings);

    public static CompiledProgram Compile(GSHFile.GX2Shader vertex, GSHFile.GX2Shader pixel,
        string? compilerPath = null)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        if (vertex.VertexHeader == null || vertex.VertexData == null)
            throw new ArgumentException("A GX2 vertex header and program are required.", nameof(vertex));
        var pixelSource = Gx2PixelShaderConversion.Prepare(pixel);
        var locations = pixelSource.InputSemantics.Order().Select((semantic, location) => (semantic, location))
            .ToDictionary(x => x.semantic, x => x.location);
        if (locations.Count > 32)
            throw new NotSupportedException("The pixel shader requires more than 32 varying locations.");
        pixelSource = new Gx2ShaderTranslator(pixel.PixelHeader, pixel.PixelData, locations).Run();
        var compiledPixel = UAMShaderCompiler.CompileByText(pixelSource.Code, "frag", compilerPath);

        // UAM's pixel header describes the components remaining after optimization.
        // Each byte at 0x18 + location contains four 2-bit interpolation modes;
        // zero means unused. See NvShaderHeader.ps.imap_generic_vector in UAM.
        var control = new ShaderLibrary.ControlShader(compiledPixel.Control);
        int offset = checked((int)control.ProgramOffset);
        var binary = compiledPixel.ShaderCode;
        if (offset < 0 || offset > binary.Length - 80 ||
            (binary[offset] & 31) != 2 || ((binary[offset + 1] >> 2) & 15) != 5)
            throw new InvalidDataException("The compiler did not produce a Maxwell pixel shader header.");
        var masks = new Dictionary<int, int>();
        foreach (var (semantic, location) in locations)
        {
            int interpolation = binary[offset + 0x18 + location], mask = 0;
            for (int component = 0; component < 4; component++)
                if (((interpolation >> (component * 2)) & 3) != 0) mask |= 1 << component;
            masks.Add(semantic, mask);
        }

        var translator = new Gx2ShaderTranslator(vertex.VertexHeader, vertex.VertexData, locations, masks);
        var translated = translator.Run();
        foreach (var (semantic, mask) in masks)
            if ((translator.VertexOutputMasks.GetValueOrDefault(semantic) & mask) != mask)
                throw new InvalidDataException($"Vertex shader does not export all pixel input components for semantic {semantic} (mask 0x{mask:X}).");

        var vertexSource = new Gx2VertexShaderConversion.PreparedShader(translated.Code,
            translator.VertexInputs.ToArray(), translator.VertexOutputs.ToArray(),
            translated.UniformBuffers, translated.Textures) { StreamOutputs = translator.StreamOutputs.ToArray() };
        var compiledVertex = UAMShaderCompiler.CompileByText(vertexSource.Code, "vert", compilerPath);
        var resultVertex = new Gx2VertexShaderConversion.CompiledShader
        {
            ShaderCode = compiledVertex.ShaderCode,
            Control = compiledVertex.Control,
            StreamOutputs = vertexSource.StreamOutputs
        };
        return new(resultVertex, compiledPixel, vertexSource, pixelSource,
            locations.Where(x => masks[x.Key] != 0)
                .Select(x => new VaryingBinding(x.Key, x.Value, masks[x.Key])).ToArray());
    }
}
