using System;
using Compiler = ShaderBuilderTool.UAMShaderCompiler;

namespace ShaderLibrary.CompileTool
{
    public class UAMShaderCompiler
    {
        // Compile source without requiring an existing target-platform binary.
        public static ShaderOutput CompileByText(string text, string kind, string? compilerPath = null)
        {
            if (!Enum.TryParse<Compiler.Kind>(kind, out var stage) || !Enum.IsDefined(stage))
                throw new ArgumentException($"Unknown shader stage: {kind}", nameof(kind));

            var result = Compiler.CompileByText(text, stage,
                compilerPath ?? Path.Combine(Path.GetDirectoryName(typeof(UAMShaderCompiler).Assembly.Location)!, "uam.exe"));
            return new ShaderOutput { ShaderCode = result.ShaderCode, Control = result.Control };
        }

        public static ShaderOutput CompileByText(BnshFile.ShaderCode binary, string text, string kind)
        {
            ArgumentNullException.ThrowIfNull(binary);
            var result = CompileByText(text, kind);
            // Only replace the existing stage after successful compilation.
            binary.ByteCode = result.ShaderCode;
            binary.ControlCode = result.Control;
            return result;
        }

        public static ShaderOutput CompileByText(BnshFile.ShaderCode binary, string text, string kind,
            Dictionary<string, string> macros)
        {
            return CompileByText(binary, GlslUtility.ApplyMacros(macros, text), kind);
        }

        public static ShaderOutput Compile(BnshFile.ShaderCode binary, string shadername, string kind)
        {
            return CompileByText(binary, File.ReadAllText(shadername), kind);
        }

        public class ShaderOutput
        {
            public byte[] ShaderCode = Array.Empty<byte>();
            public byte[] Control = Array.Empty<byte>();
        }
    }
}
