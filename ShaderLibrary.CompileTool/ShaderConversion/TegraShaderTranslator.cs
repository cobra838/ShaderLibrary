using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace EffectLibraryTest
{
    public class TegraShaderTranslator
    {
        public static string Decompile(Span<byte> bytecode)
        {
            return TranslateShader(bytecode.Slice(48, bytecode.Length - 48).ToArray());
        }

        public static string Decompile(byte[] bytecode, ShaderLibrary.ControlShader control)
            => Translate(bytecode, control).Code;

        public static ShaderProgram Translate(byte[] bytecode, ShaderLibrary.ControlShader control)
        {
            bool compute = control.ShaderStage == ShaderLibrary.ControlShader.NVNshaderStage.NVN_SHADER_STAGE_COMPUTE;
            var flags = compute ? TranslationFlags.Compute : TranslationFlags.None;
            var options = new TranslationOptions(TargetLanguage.Glsl, TargetApi.OpenGL, flags);
            var accessor = new GpuAccessor(bytecode.AsSpan(checked((int)control.ProgramOffset)).ToArray(), control);
            return Translator.CreateContext(0, accessor, options).Translate();
        }

        static string TranslateShader(byte[] data)
        {
            TranslationFlags flags = TranslationFlags.None;

            TranslationOptions translationOptions = new TranslationOptions(TargetLanguage.Glsl, TargetApi.OpenGL, flags);
            ShaderProgram program = Translator.CreateContext(0, new GpuAccessor(data), translationOptions).Translate();
            return program.Code;
        }

        private class GpuAccessor : IGpuAccessor
        {
            private readonly byte[] _data;
            private readonly ShaderLibrary.ControlShader? _control;

            public GpuAccessor(byte[] data, ShaderLibrary.ControlShader? control = null)
            {
                _data = data;
                _control = control;
            }

            public int QueryComputeLocalSizeX() => checked((int)(_control?.ShaderComp.BlockDims[0] ?? 1));
            public int QueryComputeLocalSizeY() => checked((int)(_control?.ShaderComp.BlockDims[1] ?? 1));
            public int QueryComputeLocalSizeZ() => checked((int)(_control?.ShaderComp.BlockDims[2] ?? 1));
            public int QueryComputeSharedMemorySize() => checked((int)(_control?.ShaderComp.SharedMemSz ?? 0));
            public int QueryComputeLocalMemorySize() => checked((int)(_control?.ShaderComp.LocalPosMemSz ?? 0));

            public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
            {
                return MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(_data).Slice((int)address));
            }
        }
    }
}
