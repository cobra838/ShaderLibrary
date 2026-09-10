using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using EffectLibraryTest;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool;

/// <summary>Prepare NVN compute GLSL for UAM using the source program's buffer descriptors.</summary>
public static class ComputeShaderConversion
{
    public sealed record PreparedShader(string Code, int? DriverUniformBinding);

    public static UAMShaderCompiler.ShaderOutput Compile(BnshFile.ShaderCode source, string? compilerPath = null)
    {
        return Compile(Prepare(source), compilerPath);
    }

    public static UAMShaderCompiler.ShaderOutput Compile(PreparedShader prepared, string? compilerPath = null)
    {
        return UAMShaderCompiler.CompileByText(prepared.Code, "comp", compilerPath, prepared.DriverUniformBinding);
    }

    public static PreparedShader Prepare(BnshFile.ShaderCode source)
    {
        var control = new ShaderLibrary.ControlShader(source.ControlCode);
        if (control.ShaderStage != ShaderLibrary.ControlShader.NVNshaderStage.NVN_SHADER_STAGE_COMPUTE)
            throw new ArgumentException("Expected an NVN compute shader.", nameof(source));

        var translated = TegraShaderTranslator.Translate(source.ByteCode, control);
        string code = translated.Code;
        var uniformBindings = new HashSet<int>();

        foreach (var buffer in translated.Info.SBuffers)
        {
            int offset = checked(buffer.SbCbOffset * 4);
            if (buffer.SbCbSlot != 0 || (offset & 15) != 0 || offset < 0x210 || offset >= 0x410)
                throw new NotSupportedException($"Unknown NVN compute buffer descriptor c[{buffer.SbCbSlot}][0x{offset:X}].");

            bool uniform = offset < 0x310;
            int binding = (offset - (uniform ? 0x210 : 0x310)) / 16;
            string name = $"cp_s{buffer.Slot}";
            string declaration = $@"layout\s*\([^)]*\)\s*buffer\s+_{name}\s*\{{\s*uint\s+data\[\];\s*\}}\s*{name};";

            if (uniform)
            {
                if ((buffer.Flags & Ryujinx.Graphics.Shader.BufferUsageFlags.Write) != 0)
                    throw new NotSupportedException($"The shader writes to NVN uniform buffer {binding}.");
                uniformBindings.Add(binding);
                // std140 scalar arrays have a 16-byte stride. Use uvec4 plus a lane
                // index to retain the source's tightly packed 32-bit addressing.
                code = RewriteArrayReads(code, name + ".data[", index => $"{name}_load(uint({index}))");
                code = ReplaceDeclaration(code, declaration,
                    $"layout(binding = {binding}, std140) uniform _{name} {{ uvec4 data[4096]; }} {name};\n" +
                    $"uint {name}_load(uint index) {{ return {name}.data[index >> 2u][index & 3u]; }}");
            }
            else
            {
                code = ReplaceDeclaration(code, declaration,
                    $"layout(binding = {binding}, std430) buffer _{name} {{ uint data[]; }} {name};");
            }
        }

        int? driverBinding = null;
        if (Regex.IsMatch(code, @"\bcp_c0\b"))
        {
            driverBinding = Enumerable.Range(0, 16).Where(x => !uniformBindings.Contains(x)).Select(x => (int?)x).FirstOrDefault();
            if (!driverBinding.HasValue)
                throw new NotSupportedException("No free uniform binding for the NVN driver buffer.");
            code = ReplaceDeclaration(code, UniformDeclaration("cp_c0"),
                $"layout(binding = {driverBinding}, std140) uniform _cp_c0 {{ precise vec4 data[4096]; }} cp_c0;");
        }

        if (Regex.IsMatch(code, @"\bcp_c1\b"))
        {
            byte[] constants = control.GetConstants(source.ByteCode);
            int vectors = (constants.Length + 15) / 16;
            if (vectors == 0)
                throw new InvalidDataException("Compute shader references an empty constant buffer.");
            var padded = new byte[vectors * 16];
            constants.CopyTo(padded, 0);
            var declaration = new StringBuilder($"const uvec4 cp_constants[{vectors}] = uvec4[](");
            for (int v = 0; v < vectors; v++)
            {
                if (v != 0) declaration.Append(',');
                declaration.Append("uvec4(");
                for (int lane = 0; lane < 4; lane++)
                {
                    if (lane != 0) declaration.Append(',');
                    declaration.Append($"0x{BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(v * 16 + lane * 4)):X8}u");
                }
                declaration.Append(')');
            }
            declaration.Append(");");
            code = ReplaceDeclaration(code, UniformDeclaration("cp_c1"), declaration.ToString());
            code = RewriteArrayReads(code, "cp_c1.data[", index => $"uintBitsToFloat(cp_constants[{index}])");
        }

        foreach (var buffer in translated.Info.CBuffers)
            if (buffer.Slot != 0 && buffer.Slot != 1)
                throw new NotSupportedException($"Unexpected hardware constant buffer c[{buffer.Slot}].");

        code = Regex.Replace(code, @"layout\s*\([^)]*\)\s*uniform\s+([iu]?sampler\w+)\s+(cp_t_tcb_([0-9A-Fa-f]+));", match =>
        {
            int handle = Convert.ToInt32(match.Groups[3].Value, 16);
            if (handle < 8 || (handle & 1) != 0)
                throw new NotSupportedException($"Unexpected texture handle {match.Groups[2].Value}.");
            return $"layout(binding = {(handle - 8) / 2}) uniform {match.Groups[1].Value} {match.Groups[2].Value};";
        });
        if (Regex.IsMatch(code, @"\bsupport_buffer\."))
            throw new NotSupportedException("The compute shader requires an emulator support-buffer operation.");
        code = Regex.Replace(code, @"layout\s*\([^)]*\)\s*uniform\s+_support_buffer\s*\{[^}]*\}\s*support_buffer;", "");
        return new PreparedShader(code, driverBinding);
    }

    private static string UniformDeclaration(string name) =>
        $@"layout\s*\([^)]*\)\s*uniform\s+_{name}\s*\{{[^}}]*\}}\s*{name};";

    private static string ReplaceDeclaration(string code, string pattern, string replacement)
    {
        var regex = new Regex(pattern);
        if (regex.Matches(code).Count != 1)
            throw new InvalidDataException($"Expected one generated buffer declaration: {pattern}");
        return regex.Replace(code, _ => replacement, 1);
    }

    private static string RewriteArrayReads(string code, string prefix, Func<string, string> replace)
    {
        var output = new StringBuilder();
        int cursor = 0;
        while (true)
        {
            int start = code.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (start < 0) return output.Append(code, cursor, code.Length - cursor).ToString();
            output.Append(code, cursor, start - cursor);
            int indexStart = start + prefix.Length, end = indexStart, depth = 1;
            while (end < code.Length && depth != 0)
            {
                if (code[end] == '[') depth++;
                if (code[end] == ']') depth--;
                end++;
            }
            if (depth != 0) throw new InvalidDataException("Unterminated generated buffer index.");
            string index = code.Substring(indexStart, end - indexStart - 1);
            output.Append(replace(RewriteArrayReads(index, prefix, replace)));
            cursor = end;
        }
    }
}
