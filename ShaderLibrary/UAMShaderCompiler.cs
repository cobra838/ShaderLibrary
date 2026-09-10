using ShaderLibrary;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShaderBuilderTool
{
    /// <summary>
    /// Represents a shader compiler that will compile Switch nvn shader binaries with the uam tool.
    /// Results in a shader output with byte code, control code, and symbol data.
    /// </summary>
    public class UAMShaderCompiler
    {
        static string _exeFolder => AppContext.BaseDirectory;
        static string _folder => Path.Combine(_exeFolder, "tools");

        public enum Kind // Type names based on shader file extensions.
        {
            vert, // Vertex
            frag, // Fragment
            geom, // Geometry
            comp, // Compute
            tesc, // Tess Control
            tese, // Tess Eval
        }

        /// <summary>
        /// Compiles shader code to an nvn binary.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="kind"></param>
        /// <returns></returns>
        public static ShaderOutput CompileByText(string text, Kind kind, string? compilerPath = null)
        {
            if (!Enum.IsDefined(typeof(Kind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shader stage.");
            string executable = Path.GetFullPath(compilerPath ?? Path.Combine(_folder, "uam.exe"));
            string workDirectory = Path.Combine(Path.GetTempPath(), "ShaderLibrary-uam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            try
            {
                File.WriteAllText(Path.Combine(workDirectory, "input.glsl"), text);
                return Compile(executable, workDirectory, kind);
            }
            finally
            {
                Directory.Delete(workDirectory, true);
            }
        }

        public static ShaderOutput CompileByText(string text, Kind kind, Dictionary<string, string> macros, string? compilerPath = null)
        {
            return CompileByText(GlslUtility.ApplyMacros(macros, text), kind, compilerPath);
        }

        static ShaderOutput Compile(string executable, string workDirectory, Kind kind)
        {
            ExecuteCommand(executable, workDirectory, kind);
            string programPath = Path.Combine(workDirectory, "program.bin");
            string controlPath = Path.Combine(workDirectory, "control.bin");
            if (!File.Exists(programPath) || !File.Exists(controlPath))
            {
                throw new InvalidOperationException("UAM did not produce program.bin and control.bin.");
            }

            // Raw binary and control shader
            byte[] shader_bin  = File.ReadAllBytes(programPath);
            byte[] control_bin = File.ReadAllBytes(controlPath);
            if (shader_bin.Length == 0 || control_bin.Length == 0)
                throw new InvalidOperationException("UAM produced an empty shader binary.");

            // Some UAM builds also export symbol names and bindings.
            var symbols = new ShaderSymbolData();
            string symbolsPath = Path.Combine(workDirectory, $"symbols.{kind}.json");
            if (File.Exists(symbolsPath))
            {
                symbols = JsonSerializer.Deserialize<ShaderSymbolData>(
                    File.ReadAllText(symbolsPath)) ?? new ShaderSymbolData();
            }

            foreach (var block in symbols.uniformBlocks)
            {
                if (block.stageMask == 0)
                    block.binding = 0;
            }

            return new ShaderOutput()
            {
                ShaderCode = shader_bin,
                Control = control_bin,
                Symbols = symbols,
            };
        }

        static void ExecuteCommand(string exePath, string workDirectory, Kind kind)
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Todo make a linux build to run natively
            // https://github.com/KillzXGaming/uam/tree/nvn
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                Path.GetExtension(exePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = "wine";
                info.ArgumentList.Add(exePath);
            }
            string stageArgument = kind switch {
                Kind.tesc => "tess_ctrl",
                Kind.tese => "tess_eval",
                _ => kind.ToString()
            };
            foreach (string argument in new[] {
                "--glslcbinds", "--nvnctrl=control.bin", "--nvngpu=program.bin", "-s", stageArgument, "input.glsl" })
                info.ArgumentList.Add(argument);

            using var cmd = Process.Start(info) ?? throw new InvalidOperationException("Could not start UAM.");
            var stdout = cmd.StandardOutput.ReadToEndAsync();
            var stderr = cmd.StandardError.ReadToEndAsync();
            cmd.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (cmd.ExitCode != 0)
                throw new InvalidOperationException($"UAM failed to compile {kind} (exit {cmd.ExitCode}).\n{stdout.Result}{stderr.Result}");
            if (stdout.Result.Length != 0)
                Console.Write(stdout.Result);
            if (stderr.Result.Length != 0)
                Console.Error.Write(stderr.Result);
        }


        public class ShaderOutput
        {
            /// <summary>
            /// The raw shader byte code.
            /// </summary>
            public byte[] ShaderCode;
            /// <summary>
            /// The raw shader control code.
            /// </summary>
            public byte[] Control;
            /// <summary>
            /// Symbol data serialized from json based on used shader data.
            /// </summary>
            public ShaderSymbolData Symbols = new();
        }

        public class ShaderSymbolData
        {
            public List<AttributeSymbol> inputs { get; set; } = new();
            public List<AttributeSymbol> outputs { get; set; } = new();
            public List<SamplerSymbol> samplers { get; set; } = new();
            public List<UniformBlockSymbol> uniformBlocks { get; set; } = new();
            public List<UniformBlockSymbol> storageBlocks { get; set; } = new();

            public int GetSamplerLocation(string name)
            {
                for (int i = 0; i < samplers.Count; i++)
                    if (samplers[i].name == name)
                        return samplers[i].location;
                return -1;
            }
            public int GetUniformBlockLocation(string name)
            {
                for (int i = 0; i < uniformBlocks.Count; i++)
                    if (uniformBlocks[i].name == name)
                        return uniformBlocks[i].binding - 1;
                return -1;
            }
            public int GetStorageBlockLocation(string name)
            {
                for (int i = 0; i < storageBlocks.Count; i++)
                    if (storageBlocks[i].name == name)
                        return storageBlocks[i].binding - 1;
                return -1;
            }
            public bool HasAttribute(string name) => inputs.Any(x => x.name == name);
        }

        public class AttributeSymbol
        {
            public string name { get; set; }
            public int location { get; set; }
        }
        public class SamplerSymbol
        {
            public string name { get; set; }
            public int location { get; set; }
            public int target { get; set; } // 10 == 2D
        }
        public class UniformBlockSymbol
        {
            public string name { get; set; }
            public int index { get; set; }
            public int binding { get; set; }
            public int size { get; set; }
            public int stageMask { get; set; }
            public List<UniformSymbol> uniforms { get; set; } = new();
        }
        public class UniformSymbol
        {
            public string name { get; set; }
            public int index { get; set; }
            public int offset { get; set; }
        }
    }
}
