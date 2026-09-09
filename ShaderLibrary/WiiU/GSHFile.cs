using BfshaLibrary.WiiU;
using ShaderLibrary.Common;
using ShaderLibrary.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.WiiU
{
    public class GSHFile
    {
        public struct Header
        {
            public uint Magic; //"Gfx2"
            public uint HeaderSize;
            public uint MajorVersion;
            public uint MinorVersion;
            public uint GpuVersion;
            public uint AlignMode;
        }

        public struct DataBlockHeader
        {
            public uint Magic; //"BLK{"
            public uint HeaderSize;
            public uint MajorVersion;
            public uint MinorVersion;
            public BlockType BlockType;
            public uint DataSize;
            public uint Identifier;
            public uint Index;
        }

        public List<GX2Block> Blocks = new List<GX2Block>();
        public List<GX2Shader> Shaders = new List<GX2Shader>();

        // GFD archives may store all vertex programs followed by all pixel programs.
        // Stage indices follow file order; a Shaders entry is not a declared VS/PS binding.
        public IEnumerable<GX2Shader> VertexShaders => Shaders.Where(x => x.VertexHeader != null);
        public IEnumerable<GX2Shader> PixelShaders => Shaders.Where(x => x.PixelHeader != null);
        public IEnumerable<GX2Shader> GeometryShaders => Shaders.Where(x => x.GeometryHeader != null);

        public GSHFile(string path) {
            Read(new BinaryDataReader(File.OpenRead(path), true));
        }

        public GSHFile(Stream stream) {
            Read(new BinaryDataReader(stream, true));
        }

        private void Read(BinaryDataReader reader)
        {
            var header = reader.ReadStruct<Header>();

            string magic = Encoding.ASCII.GetString(BigEndianConverter.ToBigEndian(header.Magic));
            if (magic != "Gfx2")
                throw new Exception($"Invalid signature {magic}! Expected Gfx2.");

            if (header.GpuVersion != 2)
                throw new Exception($"Unsupported GPU version {header.GpuVersion}");

            GX2Shader? currentShader = null;

            reader.SeekBegin(header.HeaderSize);
            while (reader.Position < reader.BaseStream.Length)
            {
                long start = reader.Position;

                var block = reader.ReadStruct<DataBlockHeader>();

                reader.SeekBegin(start + block.HeaderSize);
                var data = reader.ReadBytes((int)block.DataSize);

                bool isVertexHeader = block.BlockType == BlockType.VertexShaderHeader;
                bool isPixelHeader = block.BlockType == BlockType.PixelShaderHeader;
                bool isGeometryHeader = block.BlockType == BlockType.GeometryShaderHeader;
                if ((isVertexHeader || isPixelHeader || isGeometryHeader) &&
                    (currentShader == null || isVertexHeader ||
                     (isPixelHeader && currentShader.PixelHeader != null) ||
                     (isGeometryHeader && currentShader.GeometryHeader != null)))
                {
                    // Keep ordinary VS/PS/GS groups compatible, but never overwrite a stage.
                    currentShader = new GX2Shader();
                    Shaders.Add(currentShader);
                }
                if (isVertexHeader)
                    currentShader.VertexHeader = new GX2VertexHeader(new MemoryStream(data));
                if (isPixelHeader)
                    currentShader.PixelHeader = new GX2PixelHeader(new MemoryStream(data));
                if (isGeometryHeader)
                    currentShader.GeometryHeader = new GX2GeometryShaderHeader(new MemoryStream(data));

                if (block.BlockType == BlockType.VertexShaderProgram)
                {
                    if (currentShader?.VertexHeader == null || currentShader.VertexData != null)
                        throw new InvalidDataException("Vertex program without an unused vertex header.");
                    currentShader.VertexData = data;
                }
                if (block.BlockType == BlockType.PixelShaderProgram)
                {
                    if (currentShader?.PixelHeader == null || currentShader.PixelData != null)
                        throw new InvalidDataException("Pixel program without an unused pixel header.");
                    currentShader.PixelData = data;
                }
                if (block.BlockType == BlockType.GeometryShaderProgram)
                {
                    if (currentShader?.GeometryHeader == null || currentShader.GeometryData != null)
                        throw new InvalidDataException("Geometry program without an unused geometry header.");
                    currentShader.GeometryData = data;
                }

                Blocks.Add(new GX2Block()
                {
                    Header = block,
                    Data = data,
                });
            }
        }

        public class GX2Block
        {
            public DataBlockHeader Header;

            public byte[] Data;
        }

        public class GX2Shader
        {
            public GX2PixelHeader PixelHeader;
            public GX2VertexHeader VertexHeader;
            public GX2GeometryShaderHeader GeometryHeader;

            public byte[] VertexData;
            public byte[] PixelData;
            public byte[] GeometryData;

            public byte[] SaveVertexData()
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryDataWriter(mem))
                {
                    VertexHeader.align = true;
                    VertexHeader.DataSize = (uint)VertexData.Length;
                    VertexHeader.Write(writer);
                    writer.Write(VertexData);
                }
                return mem.ToArray();
            }

            public byte[] SavePixelData()
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryDataWriter(mem))
                {
                    PixelHeader.align = true;
                    PixelHeader.DataSize = (uint)PixelData.Length;
                    PixelHeader.Write(writer);
                    writer.Write(PixelData);
                }
                return mem.ToArray();
            }
            public byte[] SaveGeometryData()
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryDataWriter(mem))
                {
                    GeometryHeader.align = true;
                    GeometryHeader.DataSize = (uint)GeometryData.Length;
                    GeometryHeader.Write(writer);
                    writer.Write(GeometryData);
                }
                return mem.ToArray();
            }
        }

        public class GX2VertexHeader
        {
            GX2VertexShaderStuct ShaderRegsHeader;

            public List<GX2UniformBlock> UniformBlocks = new List<GX2UniformBlock>();
            public List<GX2UniformVar> Uniforms = new List<GX2UniformVar>();
            public List<GX2AttributeVar> Attributes = new List<GX2AttributeVar>();
            public List<GX2SamplerVar> Samplers = new List<GX2SamplerVar>();
            public List<GX2LoopVar> Loops = new List<GX2LoopVar>();

            public uint DataSize;

            public uint Mode = 1;

            public bool HasStreamOut;
            public uint StreamOutSize;

            public bool align;

            public byte[] GetRegs()
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryDataWriter(mem, true)) {
                    writer.WriteStruct(ShaderRegsHeader);
                }
                return mem.ToArray();
            }

            public bool HasBlock(string name) => this.UniformBlocks.Any(x => x.Name == name);
            public bool HasSampler(string name) => this.Samplers.Any(x => x.Name == name);
            public bool HasAttribute(string name) => this.Attributes.Any(x => x.Name == name);
            public bool HasUniform(string name) => this.Uniforms.Any(x => x.Name == name);

            public GX2VertexHeader(Stream stream, bool bigEndian = true)
            {
                this.align = !bigEndian;
                Read(new BinaryDataReader(stream, bigEndian));
            }

            public void Read(BinaryDataReader reader)
            {
                long pos = reader.Position;
                ShaderRegsHeader = reader.ReadStruct<GX2VertexShaderStuct>();

                uint size = reader.ReadUInt32();
                uint dataOffset = reader.ReadUInt32();

                Mode = reader.ReadUInt32();
                uint uniformBlockCount = reader.ReadUInt32();
                uint uniformBlocksOffset = reader.ReadUInt32() & ~0xD0600000;
                uint uniformVarCount = reader.ReadUInt32();
                uint uniformVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint padding1 = reader.ReadUInt32();
                uint padding2 = reader.ReadUInt32();

                uint loopVarCount = reader.ReadUInt32();
                uint loopVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint samplerVarCount = reader.ReadUInt32();
                uint samplerVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint attribVarCount = reader.ReadUInt32();
                uint attribVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                HasStreamOut = reader.ReadBoolean();
                StreamOutSize = reader.ReadUInt32();

                reader.SeekBegin(uniformVarsOffset);
                for (int i = 0; i < uniformVarCount; i++)
                    Uniforms.Add(new GX2UniformVar(reader));

                reader.SeekBegin(uniformBlocksOffset);
                for (int i = 0; i < uniformBlockCount; i++)
                    UniformBlocks.Add(new GX2UniformBlock(reader));

                reader.SeekBegin(attribVarsOffset);
                for (int i = 0; i < attribVarCount; i++)
                    Attributes.Add(new GX2AttributeVar(reader));

                reader.SeekBegin(samplerVarsOffset);
                for (int i = 0; i < samplerVarCount; i++)
                    Samplers.Add(new GX2SamplerVar(reader));

                reader.SeekBegin(loopVarsOffset);
                for (int i = 0; i < loopVarCount; i++)
                    Loops.Add(new GX2LoopVar(reader));
            }

            public void Write(BinaryDataWriter writer)
            {
                writer.IsWiiU = true;
                writer.WriteStruct(ShaderRegsHeader);
                writer.Write(DataSize);
                var dataOffs = writer.SaveOffset();
                writer.Write(Mode);

                writer.Write(UniformBlocks.Count);
                var uniformBlocksOffs = writer.SaveOffset();
                writer.Write(Uniforms.Count);
                var uniformVariablesOffs = writer.SaveOffset();
                writer.Write(0);
                writer.Write(0);

                writer.Write(Loops.Count);
                var loopVariablesOffs = writer.SaveOffset();

                writer.Write(Samplers.Count);
                var samplerVariablesOffs = writer.SaveOffset();

                writer.Write(Attributes.Count);
                var attribVariablesOffs = writer.SaveOffset();

                writer.Write(HasStreamOut);
                writer.Write(StreamOutSize);
                writer.AlignBytes(4);

                Dictionary<string, List<long>> stringTable = new();
                void SaveString(string str)
                {
                    if (!stringTable.ContainsKey(str))
                        stringTable.Add(str, new());
                    stringTable[str].Add(writer.Position);
                    writer.Write(0);
                }

                var mask = align ? 0xD0600000 : 0;

                if (UniformBlocks.Count > 0)
                    WriteOffset(writer, uniformBlocksOffs, mask);
                foreach (var b in UniformBlocks)
                {
                    SaveString(b.Name);
                    writer.Write(b.Offset);
                    writer.Write(b.Size);
                }
                if (Uniforms.Count > 0)
                    WriteOffset(writer, uniformVariablesOffs, mask);
                foreach (var b in Uniforms)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Count);
                    writer.Write(b.Offset);
                    writer.Write(b.BlockIndex);
                }
                if (Samplers.Count > 0)
                    WriteOffset(writer, samplerVariablesOffs, mask);
                foreach (var b in Samplers)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Location);
                }
                if (Attributes.Count > 0)
                    WriteOffset(writer, attribVariablesOffs, mask);
                foreach (var b in Attributes)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Count);
                    writer.Write(b.Location);
                }

                // Apply strings
                foreach (var str in stringTable)
                {
                    foreach (var pos in str.Value)
                        WriteOffset(writer, pos, align ? 0xCA700000 : 0);

                    writer.Write(Encoding.UTF8.GetBytes(str.Key));
                    writer.Write((byte)0);
                }

                if (Loops.Count > 0)
                    WriteOffset(writer, loopVariablesOffs, mask);
                foreach (var b in Loops)
                {
                    writer.Write(b.Offset);
                    writer.Write(b.Value);
                }

                // Align for data at the end
                writer.AlignBytes(512);
                writer.WriteOffset(dataOffs);
            }
        }

        public class GX2GeometryShaderHeader
        {
            uint[] ShaderRegsHeader;

            public List<GX2UniformBlock> UniformBlocks = new List<GX2UniformBlock>();
            public List<GX2UniformVar> Uniforms = new List<GX2UniformVar>();
            public List<GX2SamplerVar> Samplers = new List<GX2SamplerVar>();
            public List<GX2LoopVar> Loops = new List<GX2LoopVar>();

            public uint Mode = 2;
            public uint RingSize;
            public bool hasStreamOut;
            public uint[] StreamOutSize;
            public byte[] RBuffer = new byte[16];
            public uint DataSize;
            public bool align;

            public byte[] GetRegs() // 19 uint regs
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryDataWriter(mem, true))
                {
                    writer.Write(ShaderRegsHeader);
                }
                return mem.ToArray();
            }

            public bool HasBlock(string name) => this.UniformBlocks.Any(x => x.Name == name);
            public bool HasSampler(string name) => this.Samplers.Any(x => x.Name == name);
            public bool HasUniform(string name) => this.Uniforms.Any(x => x.Name == name);

            public GX2GeometryShaderHeader(Stream stream, bool bigEndian = true)
            {
                this.align = !bigEndian;
                Read(new BinaryDataReader(stream, bigEndian));
            }

            public void Read(BinaryDataReader reader)
            {
                long pos = reader.Position;
                ShaderRegsHeader = reader.ReadUInt32s(19);

                uint size = reader.ReadUInt32();
                uint dataOffset = reader.ReadUInt32();
                uint vertexSize = reader.ReadUInt32();
                uint vertexDataOffset = reader.ReadUInt32();

                Mode = reader.ReadUInt32();
                uint uniformBlockCount = reader.ReadUInt32();
                uint uniformBlocksOffset = reader.ReadUInt32() & ~0xD0600000;
                uint uniformVarCount = reader.ReadUInt32();
                uint uniformVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint padding1 = reader.ReadUInt32();
                uint padding2 = reader.ReadUInt32();

                uint loopVarCount = reader.ReadUInt32();
                uint loopVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint samplerVarCount = reader.ReadUInt32();
                uint samplerVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                RingSize = reader.ReadUInt32();
                hasStreamOut = reader.ReadUInt32() != 0;
                StreamOutSize = reader.ReadUInt32s(4);
                RBuffer = reader.ReadBytes(16);

                reader.SeekBegin(uniformVarsOffset);
                for (int i = 0; i < uniformVarCount; i++)
                    Uniforms.Add(new GX2UniformVar(reader));

                reader.SeekBegin(uniformBlocksOffset);
                for (int i = 0; i < uniformBlockCount; i++)
                    UniformBlocks.Add(new GX2UniformBlock(reader));

                reader.SeekBegin(samplerVarsOffset);
                for (int i = 0; i < samplerVarCount; i++)
                    Samplers.Add(new GX2SamplerVar(reader));

                reader.SeekBegin(loopVarsOffset);
                for (int i = 0; i < loopVarCount; i++)
                    Loops.Add(new GX2LoopVar(reader));
            }

            public void Write(BinaryDataWriter writer)
            {
                writer.IsWiiU = true;
                writer.Write(ShaderRegsHeader);
                writer.Write(DataSize);
                var dataOffs = writer.SaveOffset();
                writer.Write(0);
                writer.Write(0);
                writer.Write(Mode);

                writer.Write(UniformBlocks.Count);
                var uniformBlocksOffs = writer.SaveOffset();
                writer.Write(Uniforms.Count);
                var uniformVariablesOffs = writer.SaveOffset();

                writer.Write(0);
                writer.Write(0); 

                writer.Write(Loops.Count);
                var loopVariablesOffs = writer.SaveOffset();

                writer.Write(Samplers.Count);
                var samplerVariablesOffs = writer.SaveOffset();

                writer.Write(RingSize);
                writer.Write(hasStreamOut ? 1u : 0u);
                writer.Write(StreamOutSize);
                writer.Write(RBuffer);
                writer.AlignBytes(4);

                Dictionary<string, List<long>> stringTable = new();
                void SaveString(string str)
                {
                    if (!stringTable.ContainsKey(str))
                        stringTable.Add(str, new());
                    stringTable[str].Add(writer.Position);
                    writer.Write(0);
                }

                var mask = align ? 0xD0600000 : 0;

                if (UniformBlocks.Count > 0)
                    WriteOffset(writer, uniformBlocksOffs, mask);
                foreach (var b in UniformBlocks)
                {
                    SaveString(b.Name);
                    writer.Write(b.Offset);
                    writer.Write(b.Size);
                }
                if (Uniforms.Count > 0)
                    WriteOffset(writer, uniformVariablesOffs, mask);
                foreach (var b in Uniforms)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Count);
                    writer.Write(b.Offset);
                    writer.Write(b.BlockIndex);
                }
                if (Samplers.Count > 0)
                    WriteOffset(writer, samplerVariablesOffs, mask);
                foreach (var b in Samplers)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Location);
                }
                // Apply strings
                foreach (var str in stringTable)
                {
                    foreach (var pos in str.Value)
                        WriteOffset(writer, pos, align ? 0xCA700000 : 0);

                    writer.Write(Encoding.UTF8.GetBytes(str.Key));
                    writer.Write((byte)0);
                }
                if (Loops.Count > 0)
                    WriteOffset(writer, loopVariablesOffs, mask);
                foreach (var b in Loops)
                {
                    writer.Write(b.Offset);
                    writer.Write(b.Value);
                }

                // Align for data at the end
                writer.AlignBytes(512);
                writer.WriteOffset(dataOffs);
            }
        }

        public class GX2PixelHeader
        {
            GX2PixelShaderStuct ShaderRegsHeader;

            public List<GX2UniformBlock> UniformBlocks = new List<GX2UniformBlock>();
            public List<GX2UniformVar> Uniforms = new List<GX2UniformVar>();
            public List<GX2AttributeVar> Attributes = new List<GX2AttributeVar>();
            public List<GX2SamplerVar> Samplers = new List<GX2SamplerVar>();
            public List<GX2LoopVar> Loops = new List<GX2LoopVar>();

            public uint Mode = 1;
            public uint DataSize;
            public byte HasStreamOut;
            public uint StreamOutSize;
            public bool align;

            public byte[] GetRegs()
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryDataWriter(mem, true)) {
                    writer.WriteStruct(ShaderRegsHeader);
                }
                return mem.ToArray();
            }

            public bool HasBlock(string name) => this.UniformBlocks.Any(x => x.Name == name);
            public bool HasSampler(string name) => this.Samplers.Any(x => x.Name == name);
            public bool HasAttribute(string name) => this.Attributes.Any(x => x.Name == name);
            public bool HasUniform(string name) => this.Uniforms.Any(x => x.Name == name);


            public GX2PixelHeader(Stream stream, bool bigEndian = true)
            {
                this.align = !bigEndian;
                Read(new BinaryDataReader(stream, bigEndian));
            }

            public void Read(BinaryDataReader reader)
            {
                long pos = reader.Position;
                ShaderRegsHeader = reader.ReadStruct<GX2PixelShaderStuct>();

                DataSize = reader.ReadUInt32();
                uint dataOffset = reader.ReadUInt32();

                Mode = reader.ReadUInt32();
                uint uniformBlockCount = reader.ReadUInt32();
                uint uniformBlocksOffset = reader.ReadUInt32() & ~0xD0600000;
                uint uniformVarCount = reader.ReadUInt32();
                uint uniformVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint padding1 = reader.ReadUInt32();
                uint padding2 = reader.ReadUInt32();

                uint loopVarCount = reader.ReadUInt32();
                uint loopVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint samplerVarCount = reader.ReadUInt32();
                uint samplerVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                uint attribVarCount = reader.ReadUInt32();
                uint attribVarsOffset = reader.ReadUInt32() & ~0xD0600000;
                HasStreamOut = reader.ReadByte();
                StreamOutSize = reader.ReadUInt32();

                reader.SeekBegin(uniformVarsOffset);
                for (int i = 0; i < uniformVarCount; i++)
                    Uniforms.Add(new GX2UniformVar(reader));

                reader.SeekBegin(uniformBlocksOffset);
                for (int i = 0; i < uniformBlockCount; i++)
                    UniformBlocks.Add(new GX2UniformBlock(reader));

                reader.SeekBegin(attribVarsOffset);
                for (int i = 0; i < attribVarCount; i++)
                    Attributes.Add(new GX2AttributeVar(reader));

                reader.SeekBegin(samplerVarsOffset);
                for (int i = 0; i < samplerVarCount; i++)
                    Samplers.Add(new GX2SamplerVar(reader));

                reader.SeekBegin(loopVarsOffset);
                for (int i = 0; i < loopVarCount; i++)
                    Loops.Add(new GX2LoopVar(reader));
            }

            public void Write(BinaryDataWriter writer)
            {
                writer.IsWiiU = true;

                writer.WriteStruct(ShaderRegsHeader);
                writer.Write(DataSize);
                var dataOffs = writer.SaveOffset();
                writer.Write(Mode);

                writer.Write(UniformBlocks.Count);
                var uniformBlocksOffs = writer.SaveOffset();
                writer.Write(Uniforms.Count);
                var uniformVariablesOffs = writer.SaveOffset();
                writer.Write(0);
                writer.Write(0); 

                writer.Write(Loops.Count);
                var loopVariablesOffs = writer.SaveOffset();

                writer.Write(Samplers.Count);
                var samplerVariablesOffs = writer.SaveOffset();

                writer.Write(Attributes.Count);
                var attribVariablesOffs = writer.SaveOffset();

                writer.Write(HasStreamOut);
                writer.Write(StreamOutSize);
                writer.AlignBytes(4);

                Dictionary<string, List<long>> stringTable = new();
                void SaveString(string str)
                {
                    if (!stringTable.ContainsKey(str))
                        stringTable.Add(str, new());
                    stringTable[str].Add(writer.Position);
                    writer.Write(0);
                }

                var mask = align ? 0xD0600000 : 0;

                if (UniformBlocks.Count > 0)
                    WriteOffset(writer, uniformBlocksOffs, mask);
                foreach (var b in UniformBlocks)
                {
                    SaveString(b.Name);
                    writer.Write(b.Offset);
                    writer.Write(b.Size);
                }
                if (Uniforms.Count > 0)
                    WriteOffset(writer, uniformVariablesOffs, mask);
                foreach (var b in Uniforms)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Count);
                    writer.Write(b.Offset);
                    writer.Write(b.BlockIndex);
                }
                if (Samplers.Count > 0)
                    WriteOffset(writer, samplerVariablesOffs, mask);
                foreach (var b in Samplers)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Location);
                }
                if (Attributes.Count > 0)
                    WriteOffset(writer, attribVariablesOffs, mask);
                foreach (var b in Attributes)
                {
                    SaveString(b.Name);
                    writer.Write((uint)b.Type);
                    writer.Write(b.Count);
                    writer.Write(b.Location);
                }
                // Apply strings
                foreach (var str in stringTable)
                {
                    foreach (var pos in str.Value)
                        WriteOffset(writer, pos, align ? 0xCA700000 : 0); 

                    writer.Write(Encoding.UTF8.GetBytes(str.Key));
                    writer.Write((byte)0);
                }
                if (Loops.Count > 0)
                    WriteOffset(writer, loopVariablesOffs, mask);
                foreach (var b in Loops)
                {
                    writer.Write(b.Offset);
                    writer.Write(b.Value);
                }

                // Align for data at the end
                writer.AlignBytes(512);
                writer.WriteOffset(dataOffs);
            }
        }

        static void WriteOffset(BinaryDataWriter writer, long target, uint mask)
        {
            var pos = writer.Position;
            if (mask != 0)
                pos |= mask;

            using (writer.BaseStream.TemporarySeek(target, SeekOrigin.Begin)) {
                writer.Write((uint)pos);
            }
        }

        static string ReadNameOffset(BinaryDataReader reader)
        {
            var offset = reader.ReadUInt32() & ~0xCA700000;
            return reader.ReadCustom(() => reader.ReadZeroTerminatedString(), (uint)offset);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        class GX2VertexShaderStuct
        {
            public uint sq_pgm_resources_vs;
            public uint vgt_primitiveid_en;
            public uint spi_vs_out_config;
            public uint num_spi_vs_out_id;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public uint[] spi_vs_out_id;

            public uint pa_cl_vs_out_cntl;
            public uint sq_vtx_semantic_clear;
            public uint num_sq_vtx_semantic;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public uint[] sq_vtx_semantic;

            public uint vgt_strmout_buffer_en;
            public uint vgt_vertex_reuse_block_cntl;
            public uint vgt_hos_reuse_depth;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        class GX2PixelShaderStuct
        {
            public uint sq_pgm_resources_ps;
            public uint sq_pgm_exports_ps;
            public uint spi_ps_in_control_0;
            public uint spi_ps_in_control_1;
            public uint num_spi_ps_input_cntl;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public uint[] spi_ps_input_cntls;

            public uint cb_shader_mask;
            public uint cb_shader_control;
            public uint db_shader_control;
            public uint spi_input_z;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        class GX2GeometryShaderStuct
        {
            public uint sq_pgm_resources_ps;
            public uint sq_pgm_exports_ps;
            public uint spi_ps_in_control_0;
            public uint spi_ps_in_control_1;
            public uint num_spi_ps_input_cntl;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public uint[] spi_ps_input_cntls;

            public uint cb_shader_mask;
            public uint cb_shader_control;
            public uint db_shader_control;
            public uint spi_input_z;
        }

        public class GX2UniformVar
        {
            public string Name { get; set; }
            public GX2ShaderVarType Type { get; set; }
            public uint Count { get; set; }
            public uint Offset { get; set; }
            public uint BlockIndex { get; set; }

            public GX2UniformVar() { }

            public GX2UniformVar(BinaryDataReader reader)
            {
                Name = ReadNameOffset(reader);
                Type = (GX2ShaderVarType)reader.ReadUInt32();
                Count = reader.ReadUInt32();
                Offset = reader.ReadUInt32();
                BlockIndex = reader.ReadUInt32();
            }
        }

        public class GX2SamplerVar
        {
            public string Name { get; set; }
            public GX2SamplerVarType Type { get; set; }
            public uint Location { get; set; }

            public GX2SamplerVar() { }

            public GX2SamplerVar(BinaryDataReader reader)
            {
                Name = ReadNameOffset(reader);
                Type = (GX2SamplerVarType)reader.ReadUInt32();
                Location = reader.ReadUInt32();
            }
        }

        public class GX2UniformBlock
        {
            public string Name { get; set; }
            public uint Offset { get; set; }
            public uint Size { get; set; }

            public GX2UniformBlock() { }

            public GX2UniformBlock(BinaryDataReader reader)
            {
                Name = ReadNameOffset(reader);
                Offset = reader.ReadUInt32();
                Size = reader.ReadUInt32();
            }
        }

        public class GX2AttributeVar
        {
            public string Name { get; set; }
            public GX2ShaderVarType Type { get; set; }
            public uint Count { get; set; }
            public int Location { get; set; }

            public GX2AttributeVar() { }

            public GX2AttributeVar(BinaryDataReader reader)
            {
                Name = ReadNameOffset(reader);
                Type = (GX2ShaderVarType)reader.ReadUInt32();
                Count = reader.ReadUInt32();
                Location = reader.ReadInt32();
            }

            public uint GetStreamCount()
            {
                return GetStreamCount(Type);
            }

            static uint GetStreamCount(GX2ShaderVarType type)
            {
                switch (type)
                {
                    case GX2ShaderVarType.INT:
                    case GX2ShaderVarType.INT2:
                    case GX2ShaderVarType.INT3:
                    case GX2ShaderVarType.INT4:
                    case GX2ShaderVarType.FLOAT:
                    case GX2ShaderVarType.FLOAT2:
                    case GX2ShaderVarType.FLOAT3:
                    case GX2ShaderVarType.FLOAT4:
                        return 1;
                    case GX2ShaderVarType.FLOAT4X4:
                        return 4;
                    default:
                        throw new Exception("Unsupported/Invalid GX2ShaderVarType");
                }
            }
        }

        public class GX2LoopVar
        {
            public uint Offset { get; set; }
            public uint Value { get; set; }

            public GX2LoopVar() { }

            public GX2LoopVar(BinaryDataReader reader)
            {
                Offset = reader.ReadUInt32();
                Value = reader.ReadUInt32();
            }
        }

        public enum BlockType : uint
        {
            Invalid = 0x00,
            EndOfFile = 0x01,
            AlignData = 0x02,
            VertexShaderHeader = 0x03,
            VertexShaderProgram = 0x05,
            PixelShaderHeader = 0x06,
            PixelShaderProgram = 0x07,
            GeometryShaderHeader = 0x08,
            GeometryShaderProgram = 0x09,
            GeometryShaderProgram2 = 0x10,
            ImageInfo = 0x11,
            ImageData = 0x12,
            MipData = 0x13,
            ComputeShaderHeader = 0x14,
            ComputeShader = 0x15,
            UserBlock = 0x16,
        }
    }
}
