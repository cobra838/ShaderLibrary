// SPDX-License-Identifier: MPL-2.0
// Latte instruction decoding adapted from Cemu. See Libs/Cemu License.txt
// and the CompileTool README for the source revision and files.
using System.Buffers.Binary;
using System.Text;
using ShaderLibrary.Common;
using ShaderLibrary.WiiU;
using static ShaderLibrary.CompileTool.Gx2PixelShaderConversion;

namespace ShaderLibrary.CompileTool;

internal sealed class Gx2ShaderTranslator(GSHFile.GX2PixelHeader header, byte[] code)
{
    private readonly GSHFile.GX2VertexHeader? vertexHeader;
    internal Gx2ShaderTranslator(GSHFile.GX2VertexHeader header, byte[] code) : this((GSHFile.GX2PixelHeader)null!, code)
    {
        vertexHeader = header;
    }
    private bool IsVertex => vertexHeader != null;
    private uint Mode => IsVertex ? vertexHeader!.Mode : header.Mode;
    private List<GSHFile.GX2UniformBlock> UniformBlocks => IsVertex ? vertexHeader!.UniformBlocks : header.UniformBlocks;
    private List<GSHFile.GX2SamplerVar> Samplers => IsVertex ? vertexHeader!.Samplers : header.Samplers;
    internal List<Gx2VertexShaderConversion.VertexAttribute> VertexInputs { get; } = new();
    internal SortedSet<int> VertexOutputs { get; } = new();
    private bool vertexInputsRead;
    private bool positionWritten;
    private bool usesFiniteReciprocal;
    private readonly StringBuilder body = new();
    private readonly SortedSet<int> colors = new();
    private readonly SortedDictionary<int, UniformBinding> buffers = new();
    private readonly SortedDictionary<int, TextureBinding> textures = new();
    private sealed class Branch(HashSet<int> previous)
    {
        public int? Target;
        public HashSet<int> OnEntry = previous;
        public HashSet<int>? AfterThen;
    }
    private readonly Stack<Branch> branches = new();
    private readonly HashSet<int> undefinedPrevious = new();
    private int instructionOffset;
    private int groupId;

    private static int Bits(uint word, int shift, int width) => (int)((word >> shift) & ((1u << width) - 1));
    private static string Hex(uint word) => $"0x{word:X8}u";
    private static char Channel(int channel) => "xyzw"[channel];
    private Exception Unsupported(string what) => new NotSupportedException($"GX2 {(IsVertex ? "vertex" : "pixel")} shader at 0x{instructionOffset:X}: {what}");
    private void Require(bool condition, string what)
    {
        if (!condition) throw Unsupported(what);
    }
    private uint Word(int offset)
    {
        if (offset < 0 || offset > code.Length - 4)
            throw new InvalidDataException($"Truncated GX2 instruction at 0x{offset:X}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(offset, 4));
    }

    public PreparedShader Run()
    {
        Require(Mode == 1, $"shader mode {Mode}");
        Require(code.Length > 0 && code.Length % 8 == 0, "unaligned program");
        if (IsVertex)
            Require(VertexReg(1) == 0 && VertexReg(49) == 0, "primitive ID or stream output");
        bool ended = false;
        for (int offset = 0; offset < code.Length; offset += 8)
        {
            instructionOffset = offset;
            uint a = Word(offset), b = Word(offset + 4);
            int op = Bits(b, 23, 7);
            if (op >= 0x40)
            {
                int aluKind = Bits(b, 26, 4);
                Require(aluKind is 8 or 9 or 10 or 11, $"control-flow ALU operation {aluKind:X}");
                EmitAlu(a, b, aluKind == 9);
                if (aluKind is 10 or 11)
                {
                    int pops = aluKind == 10 ? 1 : 2;
                    for (int i = 0; i < pops; i++)
                    {
                        Require(branches.Count != 0 && branches.Peek().Target == offset / 8 + 1, "unmatched ALU stack pop");
                        var branch = branches.Pop();
                        undefinedPrevious.UnionWith(branch.AfterThen ?? branch.OnEntry);
                        body.AppendLine("}");
                    }
                }
            }
            else
            {
                switch (op)
                {
                    case 0:
                        Require((b & 7) == 0, "NOP with stack pop");
                        break;
                    case 0x27:
                    case 0x28:
                        EmitExport(a, b);
                        break;
                    case 1:
                        Require(Bits(b, 8, 2) == 0, "conditional texture clause");
                        EmitTextures(a, Bits(b, 10, 3) + Bits(b, 19, 1) * 8 + 1);
                        break;
                    case 0x13:
                        Require(IsVertex && Bits(b, 8, 2) == 0 && (b & 7) == 0, "conditional/non-vertex CALL_FS");
                        ReadVertexInputs();
                        foreach (var attribute in VertexInputs)
                            body.AppendLine($"r[{attribute.Register}] = floatBitsToUint(gx2_attr{attribute.Location});");
                        break;
                    case 0x0A:
                        int popCount = Bits(b, 0, 3);
                        Require(Bits(b, 8, 2) == 0 && popCount <= branches.Count &&
                            branches.Count != 0 && branches.Peek().Target == null &&
                            a > offset / 8 && a < code.Length / 8, "unstructured JUMP");
                        if (popCount == 0)
                            Require(Bits(Word(checked((int)a * 8 + 4)), 23, 7) == 0x0D, "JUMP without pop must target ELSE");
                        else
                            Require(branches.Skip(1).Take(popCount - 1).All(x => x.Target == (int)a), "JUMP across mismatched outer blocks");
                        branches.Peek().Target = (int)a;
                        break;
                    case 0x0D:
                        Require(branches.Count != 0 && branches.Peek().Target == offset / 8 &&
                            branches.Peek().AfterThen == null && Bits(b, 8, 2) == 0 && Bits(b, 0, 3) == 1 &&
                            a > offset / 8 && a < code.Length / 8, "unstructured ELSE");
                        var alternate = branches.Peek();
                        alternate.AfterThen = new(undefinedPrevious);
                        alternate.Target = (int)a;
                        undefinedPrevious.Clear();
                        undefinedPrevious.UnionWith(alternate.OnEntry);
                        body.AppendLine("} else {");
                        break;
                    case 0x0E:
                        int pops = Bits(b, 0, 3);
                        Require(Bits(b, 8, 2) == 0 && pops > 0 && pops <= branches.Count &&
                            a == offset / 8 + 1, "unstructured POP");
                        for (int i = 0; i < pops; i++)
                        {
                            Require(branches.Peek().Target == (int)a, "POP across mismatched blocks");
                            var branch = branches.Pop();
                            undefinedPrevious.UnionWith(branch.AfterThen ?? branch.OnEntry);
                            body.AppendLine("}");
                        }
                        break;
                    default: throw Unsupported($"control-flow operation {op:X}");
                }
                if ((b & (1u << 21)) != 0) { ended = true; break; }
            }
        }
        if (!ended) throw new InvalidDataException("GX2 program has no end instruction.");
        Require(branches.Count == 0, "unterminated conditional block");
        Require(!IsVertex || positionWritten, "vertex program without a position export");

        var declarations = new StringBuilder("#version 450\n");
        var imports = new StringBuilder();
        int[] semantics;
        if (IsVertex)
        {
            foreach (var attribute in VertexInputs.DistinctBy(x => x.Location))
                declarations.AppendLine($"layout(location = {attribute.Location}) in vec4 gx2_attr{attribute.Location};");
            foreach (int semantic in VertexOutputs)
                declarations.AppendLine($"layout(location = {semantic}) out vec4 gx2_sem{semantic};");
            imports.AppendLine("r[0] = uvec4(uint(gl_VertexID), 0u, 0u, uint(gl_InstanceID));");
            semantics = VertexInputs.Select(x => x.Location).ToArray();
        }
        else semantics = EmitInputs(declarations, imports);
        foreach (int output in colors)
            declarations.AppendLine($"layout(location = {output}) out vec4 gx2_color{output};");
        foreach (var (binding, buffer) in buffers)
            declarations.AppendLine($"layout(std140, binding = {binding}) uniform GX2Block{binding} {{ uvec4 data[{(buffer.Size + 15) / 16}]; }} gx2_cb{binding};");
        foreach (var (binding, texture) in textures)
            declarations.AppendLine($"layout(binding = {binding}) uniform {SamplerType(texture.Type)} gx2_tex{binding};");
        // DX9 MUL returns zero when either operand is zero, including 0 * NaN.
        declarations.AppendLine("float gx2_mul(float a, float b) { return a == 0.0 || b == 0.0 ? 0.0 : a * b; }");
        declarations.AppendLine("float gx2_log(float a) { float v = log2(max(0.0, a)); return isinf(v) ? uintBitsToFloat(0xFF7FFFFFu) : v; }");
        if (usesFiniteReciprocal)
            // RECIP_FF and RECIPSQRT_FF flush infinity to signed zero.
            declarations.AppendLine("float gx2_flush_inf(float v) { return isinf(v) ? uintBitsToFloat(floatBitsToUint(v) & 0x80000000u) : v; }");
        declarations.AppendLine("void main() {\nuvec4 r[128];\nuvec4 pv;\nuint ps;");
        declarations.Append(imports).Append(body).AppendLine("}");
        return new(declarations.ToString(), semantics, colors.ToArray(), buffers.Values.ToArray(), textures.Values.ToArray());
    }

    private static string SamplerType(GX2SamplerVarType type) => type switch
    {
        GX2SamplerVarType.SAMPLER_2D => "sampler2D",
        GX2SamplerVarType.SAMPLER_3D => "sampler3D",
        GX2SamplerVarType.SAMPLER_2D_SHADOW => "sampler2DShadow",
        GX2SamplerVarType.SAMPLER_2D_ARRAY_SHADOW => "sampler2DArrayShadow",
        _ => throw new NotSupportedException($"GX2 texture type {type}")
    };

    private void EmitTextures(uint address, int count)
    {
        int start = checked((int)address * 8);
        for (int i = 0; i < count; i++)
        {
            instructionOffset = checked(start + i * 16);
            uint a = Word(instructionOffset), b = Word(instructionOffset + 4), c = Word(instructionOffset + 8);
            _ = Word(instructionOffset + 12);
            int op = Bits(a, 0, 5), binding = Bits(a, 8, 8), sampler = Bits(c, 15, 5);
            if (op == 0)
            {
                EmitUniformFetch(a, b, c);
                continue;
            }
            Require(op is 0x0F or 0x10 or 0x11 or 0x13 or 0x18 or 0x1B, $"texture operation {op:X}");
            Require(Bits(a, 5, 2) == 0, "texture instruction modifiers");
            Require(binding == sampler, "separate texture/sampler bindings");
            Require((a & 0x800000) == 0 && (b & 0x80) == 0, "indexed texture register");
            Require((c & 0x7FFF) == 0 && Bits(b, 21, 7) == 0, "texture offset or LOD bias");
            var matches = Samplers.Where(x => x.Location == binding).ToArray();
            Require(matches.Length == 1, $"missing/duplicate sampler {binding}");
            var texture = matches[0];
            _ = SamplerType(texture.Type);
            bool shadow = op is 0x18 or 0x1B;
            bool shadowArray = texture.Type == GX2SamplerVarType.SAMPLER_2D_ARRAY_SHADOW;
            Require(shadow == (shadowArray || texture.Type == GX2SamplerVarType.SAMPLER_2D_SHADOW),
                "texture comparison operation/type mismatch");
            int dimensions = texture.Type == GX2SamplerVarType.SAMPLER_3D ? 3 : 2;
            Require(op != 0x0F || dimensions == 2, "non-2D FETCH4");
            for (int n = 0; n < dimensions; n++)
                Require(Bits(b, 28 + n, 1) == 1, "unnormalized texture coordinates");
            textures[binding] = new(texture.Name, texture.Location, texture.Type);
            int source = Bits(a, 16, 7), dest = Bits(b, 0, 7);
            string Coordinate(int component)
            {
                int select = Bits(c, 20 + component * 3, 3);
                Require(select <= 5, "reserved texture source selector");
                return select < 4 ? $"uintBitsToFloat(r[{source}].{Channel(select)})" : select == 4 ? "0.0" : "1.0";
            }
            string coords = $"vec{dimensions}({string.Join(", ", Enumerable.Range(0, dimensions).Select(Coordinate))})";
            if (shadow)
                coords = shadowArray ? $"vec4({Coordinate(0)}, {Coordinate(1)}, {Coordinate(2)}, {Coordinate(3)})" :
                    $"vec3({Coordinate(0)}, {Coordinate(1)}, {Coordinate(3)})";
            // FETCH4 orders its four texels differently from GLSL gather.
            string call = op == 0x0F ? $"textureGather(gx2_tex{binding}, {coords}).zxyw" :
                op is 0x10 or 0x18 ? $"texture(gx2_tex{binding}, {coords})" :
                $"textureLod(gx2_tex{binding}, {coords}, {(op is 0x13 or 0x1B ? "0.0" : Coordinate(3))})";
            // Preserve source registers until the entire sampled vector is ready.
            string result = $"tex_{instructionOffset:X}";
            body.AppendLine($"uvec4 {result} = {(shadow ? $"uvec4(floatBitsToUint({call}))" : $"floatBitsToUint({call})")};");
            for (int n = 0; n < 4; n++)
            {
                int select = Bits(b, 9 + n * 3, 3);
                if (select == 7) continue;
                Require(select <= 5, "reserved texture destination selector");
                Require(!shadow || select is 0 or 4 or 5, "undefined shadow sample result component");
                string value = select < 4 ? $"{result}.{Channel(select)}" : select == 4 ? "0u" : "0x3F800000u";
                body.AppendLine($"r[{dest}].{Channel(n)} = {value};");
            }
        }
    }

    private void EmitUniformFetch(uint a, uint b, uint c)
    {
        // VFETCH in a texture clause can address a uniform block, using a
        // register as a vec4 index. This is not an external vertex-buffer fetch.
        int binding = Bits(a, 8, 8) - 0x80;
        Require(binding is >= 0 and < 16 && Bits(a, 5, 2) == 2, "non-uniform VFETCH");
        Require(Bits(a, 7, 1) == 0 && Bits(a, 23, 1) == 0 && Bits(b, 7, 2) == 0,
            "indexed/whole-quad VFETCH");
        Require(Bits(a, 26, 6) == 15 && Bits(b, 21, 11) == 1 && c == 0x80000,
            "VFETCH format, stride, offset or endian mode");
        var matches = UniformBlocks.Where(x => x.Offset == binding).ToArray();
        Require(matches.Length == 1 && matches[0].Size > 0 && matches[0].Size % 16 == 0,
            $"missing/invalid VFETCH uniform binding {binding}");
        var buffer = matches[0];
        buffers[binding] = new(buffer.Name, buffer.Offset, buffer.Size);
        int source = Bits(a, 16, 7), component = Bits(a, 24, 2), dest = Bits(b, 0, 7);
        string result = $"fetch_{instructionOffset:X}";
        body.AppendLine($"uvec4 {result} = gx2_cb{binding}.data[int(r[{source}].{Channel(component)})];");
        for (int n = 0; n < 4; n++)
        {
            int select = Bits(b, 9 + n * 3, 3);
            if (select == 7) continue;
            Require(select <= 5, "reserved VFETCH destination selector");
            string value = select < 4 ? $"{result}.{Channel(select)}" : select == 4 ? "0u" : "0x3F800000u";
            body.AppendLine($"r[{dest}].{Channel(n)} = {value};");
        }
    }

    private int[] EmitInputs(StringBuilder declarations, StringBuilder imports)
    {
        byte[] regs = header.GetRegs();
        uint Reg(int index) => BinaryPrimitives.ReadUInt32BigEndian(regs.AsSpan(index * 4, 4));
        uint control0 = Reg(2), control1 = Reg(3);
        int count = Bits(control0, 0, 6);
        Require(count <= 32 && count <= Reg(4), "invalid pixel input count");
        // Position and point-coordinate inputs need the rasterization convention
        // of the eventual target pipeline, not an emulator viewport transform.
        Require((control0 & 0x0007FF00u) == 0, "position/point-coordinate input");
        Require((control1 & 0x100) == 0, "front-face input");
        var semantics = new List<int>();
        for (int i = 0; i < count; i++)
        {
            uint input = Reg(5 + i);
            int semantic = Bits(input, 0, 8);
            Require(semantic < 128 && !semantics.Contains(semantic), "special or duplicate input semantic");
            Require((input & ~0x1FFFu) == 0, "pixel input control flags");
            bool flat = (input & 0x400) != 0;
            string qualifiers = flat ? "flat " : ((input & 0x1000) != 0 ? "noperspective " : "");
            if (!flat && (input & 0x800) != 0) qualifiers += "centroid ";
            declarations.AppendLine($"layout(location = {semantic}) {qualifiers}in vec4 gx2_sem{semantic};");
            imports.AppendLine($"r[{i}] = floatBitsToUint(gx2_sem{semantic});");
            semantics.Add(semantic);
        }
        return semantics.ToArray();
    }

    private void EmitExport(uint a, uint b)
    {
        if (IsVertex) { EmitVertexExport(a, b); return; }
        Require(Bits(a, 13, 2) == 0, "non-pixel export");
        Require(Bits(a, 22, 8) == 0, "indexed export");
        int first = Bits(a, 0, 13), gpr = Bits(a, 15, 7), burst = Bits(b, 17, 4);
        Require(first + burst < 8 && gpr + burst < 128, "depth or out-of-range export");
        for (int i = 0; i <= burst; i++)
        {
            colors.Add(first + i);
            for (int c = 0; c < 4; c++)
            {
                int sel = Bits(b, c * 3, 3);
                if (sel == 7) continue; // masked component
                Require(sel <= 5, "reserved export selector");
                string value = sel < 4 ? $"uintBitsToFloat(r[{gpr + i}].{Channel(sel)})" : sel == 4 ? "0.0" : "1.0";
                body.AppendLine($"gx2_color{first + i}.{Channel(c)} = {value};");
            }
        }
    }

    private byte[]? vertexRegisters;
    private uint VertexReg(int index) => BinaryPrimitives.ReadUInt32BigEndian((vertexRegisters ??= vertexHeader!.GetRegs()).AsSpan(index * 4, 4));

    private void ReadVertexInputs()
    {
        if (vertexInputsRead) return;
        uint count = VertexReg(16), clear = VertexReg(15);
        Require(count <= 32, "vertex semantic table size");
        for (int slot = 0; slot < 32; slot++)
        {
            if ((clear & (1u << slot)) != 0) continue;
            Require(slot < count, "vertex semantic outside the declared table");
            uint semantic = VertexReg(17 + slot);
            var matches = vertexHeader!.Attributes.Where(x => x.Location == semantic).ToArray();
            Require(matches.Length == 1, $"missing/duplicate vertex attribute {semantic}");
            var attribute = matches[0];
            Require(attribute.Location is >= 0 and < 32 &&
                attribute.Type is GX2ShaderVarType.FLOAT3 or GX2ShaderVarType.FLOAT4 && attribute.Count <= 1,
                $"vertex attribute type/location for {attribute.Name}");
            VertexInputs.Add(new(attribute.Name, attribute.Location, attribute.Type, slot + 1));
        }
        vertexInputsRead = true;
    }

    private void EmitVertexExport(uint a, uint b)
    {
        Require(Bits(a, 22, 8) == 0, "indexed vertex export");
        int type = Bits(a, 13, 2), first = Bits(a, 0, 13), gpr = Bits(a, 15, 7), burst = Bits(b, 17, 4);
        Require(gpr + burst < 128, "vertex export register range");
        for (int i = 0; i <= burst; i++)
        {
            string target;
            if (type == 1 && first + i == 60) { target = "gl_Position"; positionWritten = true; }
            else if (type == 2 && first + i < 32)
            {
                int param = first + i;
                Require(param / 4 < VertexReg(3), "vertex output outside semantic table");
                int semantic = Bits(VertexReg(4 + param / 4), (param % 4) * 8, 8);
                if (semantic == 255) continue; // GX2 disables this parameter export.
                Require(semantic < 32, "vertex output semantic range");
                VertexOutputs.Add(semantic);
                target = $"gx2_sem{semantic}";
            }
            else throw Unsupported($"vertex export type {type}, index {first + i}");
            for (int c = 0; c < 4; c++)
            {
                int sel = Bits(b, c * 3, 3);
                if (sel == 7) continue;
                Require(sel <= 5, "reserved vertex export selector");
                string value = sel < 4 ? $"uintBitsToFloat(r[{gpr + i}].{Channel(sel)})" : sel == 4 ? "0.0" : "1.0";
                body.AppendLine($"{target}.{Channel(c)} = {value};");
            }
        }
    }

    private sealed record Alu(uint A, uint B, int Unit, int Offset)
    {
        public bool Op3 => Bits(B, 13, 5) >= 8;
        public int Op => Op3 ? Bits(B, 13, 5) : Bits(B, 7, 11);
        public int Destination => Bits(B, 21, 7);
        public int Component => Bits(B, 29, 2);
        public bool Write => Op3 || (B & 16) != 0;
        public bool Predicate => !Op3 && IsPredicate(Op);
        public bool Nop => !Op3 && Op == 0x1A;
    }

    private static bool IsPredicate(int op) => op is >= 0x20 and <= 0x23 or >= 0x42 and <= 0x45;

    private void EmitAlu(uint cf0, uint cf1, bool push)
    {
        int offset = Bits(cf0, 0, 22) * 8;
        int end = checked(offset + (Bits(cf1, 18, 7) + 1) * 8);
        if (end > code.Length) throw new InvalidDataException("GX2 ALU clause outside the program.");
        bool predicateEmitted = false;
        while (offset < end)
        {
            var group = new List<Alu>();
            int units = 0, literalCount = 0;
            bool last = false;
            do
            {
                instructionOffset = offset;
                if (offset >= end) throw new InvalidDataException("Unterminated GX2 ALU group.");
                uint a = Word(offset), b = Word(offset + 4);
                bool op3 = Bits(b, 13, 5) >= 8;
                int op = op3 ? Bits(b, 13, 5) : Bits(b, 7, 11);
                Require(Bits(a, 29, 2) == 0 && Bits(b, 28, 1) == 0, "predicated ALU or indexed destination");
                bool predicate = !op3 && IsPredicate(op);
                Require(op3 || (predicate ?
                    (push && (b & 28) == 12 && Bits(b, 5, 2) == 0 && (b & 0x80000000) == 0) : (b & 12) == 0),
                    "ALU predicate/execute-mask update");
                int unit = Bits(b, 29, 2);
                if (!op3 && IsTranscendental(op) || (units & (1 << unit)) != 0) unit = 4;
                Require((units & (1 << unit)) == 0, "duplicate ALU unit");
                units |= 1 << unit;
                var inst = new Alu(a, b, unit, offset);
                group.Add(inst);
                if (Bits(a, 0, 9) == 253) literalCount = Math.Max(literalCount, Bits(a, 10, 2) + 1);
                if (Bits(a, 13, 9) == 253) literalCount = Math.Max(literalCount, Bits(a, 23, 2) + 1);
                if (op3 && Bits(b, 0, 9) == 253) literalCount = Math.Max(literalCount, Bits(b, 10, 2) + 1);
                offset += 8;
                last = (a & 0x80000000) != 0;
            } while (!last);
            var literals = new uint[4];
            int literalBytes = ((literalCount + 1) / 2) * 8;
            if (offset + literalBytes > end) throw new InvalidDataException("Truncated GX2 ALU literals.");
            for (int i = 0; i < literalCount; i++) literals[i] = Word(offset + i * 4);
            offset += literalBytes;
            var predicates = group.Where(x => x.Predicate).ToArray();
            Require(predicates.Length == 0 || (push && !predicateEmitted && predicates.Length == 1), "multiple/unscoped predicates");
            var reduction = group.Where(x => !x.Op3 && x.Op is 0x50 or 0x51).OrderBy(x => x.Unit).ToArray();
            if (reduction.Length != 0)
            {
                Require(reduction.Length == 4 && reduction.Select(x => x.Unit).SequenceEqual(new[] { 0, 1, 2, 3 }) &&
                    reduction.All(x => x.Op == reduction[0].Op), "incomplete/mixed DOT4 group");
                // DOT4 consumes all four vector units. The scalar unit may
                // execute another instruction concurrently, using old PV/PS.
                var products = reduction.Select(x =>
                {
                    string a = $"uintBitsToFloat({Source(x, 0, cf0, cf1, literals)})";
                    string b = $"uintBitsToFloat({Source(x, 1, cf0, cf1, literals)})";
                    return x.Op == 0x50 ? $"gx2_mul({a}, {b})" : $"({a} * {b})";
                });
                body.AppendLine($"precise float dot{groupId} = {string.Join(" + ", products)};");
            }
            // Every operation reads the state BEFORE the group. Commit writes
            // only after all results, including PV/PS, have been evaluated.
            foreach (var inst in group)
            {
                instructionOffset = inst.Offset;
                if (inst.Nop)
                {
                    Require(!inst.Write && Bits(inst.B, 5, 2) == 0 && (inst.B & 0x80000000) == 0,
                        "NOP with result modifiers/write mask");
                    continue;
                }
                if (predicates.Contains(inst))
                {
                    bool integer = inst.Op >= 0x42;
                    string cast = integer ? "int" : "uintBitsToFloat";
                    string left = $"{cast}({Source(inst, 0, cf0, cf1, literals, integer)})";
                    string right = $"{cast}({Source(inst, 1, cf0, cf1, literals, integer)})";
                    string compare = new[] { "==", ">", ">=", "!=" }[inst.Op - (integer ? 0x42 : 0x20)];
                    body.AppendLine($"bool pred{groupId} = {left} {compare} {right};");
                    continue;
                }
                string value = reduction.Contains(inst) ? $"floatBitsToUint(dot{groupId})" : Evaluate(inst, cf0, cf1, literals);
                int omod = inst.Op3 ? 0 : Bits(inst.B, 5, 2);
                if (omod != 0) value = $"floatBitsToUint(uintBitsToFloat({value}) * {(omod == 1 ? "2.0" : omod == 2 ? "4.0" : "0.5")})";
                if ((inst.B & 0x80000000) != 0) value = $"floatBitsToUint(clamp(uintBitsToFloat({value}), 0.0, 1.0))";
                body.AppendLine($"precise uint t{groupId}_{inst.Unit} = {value};");
            }
            foreach (var inst in group)
            {
                if (inst.Nop || predicates.Contains(inst))
                {
                    // NOP and predicate instructions have no ordinary result in this
                    // lowering. Reject a later read instead of inventing PV.
                    undefinedPrevious.Add(inst.Unit);
                    continue;
                }
                string value = $"t{groupId}_{inst.Unit}";
                if (inst.Write) body.AppendLine($"r[{inst.Destination}].{Channel(inst.Component)} = {value};");
                body.AppendLine($"{(inst.Unit == 4 ? "ps" : $"pv.{Channel(inst.Unit)}")} = {value};");
                undefinedPrevious.Remove(inst.Unit);
            }
            if (predicates.Length != 0)
            {
                branches.Push(new(new(undefinedPrevious)));
                body.AppendLine($"if (pred{groupId}) {{");
                predicateEmitted = true;
            }
            groupId++;
        }
        Require(!push || predicateEmitted, "ALU_PUSH_BEFORE without a supported predicate");
    }

    private static bool IsTranscendental(int op) => op is 0x61 or 0x62 or 0x63 or 0x65 or 0x66 or 0x67 or 0x68 or 0x69 or 0x6A or 0x6B or 0x6C or 0x6D or 0x6E or 0x6F or 0x73 or 0x75 or 0x79;

    private string Source(Alu inst, int index, uint cf0, uint cf1, uint[] literals, bool integer = false)
    {
        uint word = index == 2 ? inst.B : inst.A;
        int shift = index == 1 ? 13 : 0;
        int sel = Bits(word, shift, 9), chan = Bits(word, shift + 10, 2);
        Require(Bits(word, shift + 9, 1) == 0, "relative ALU source");
        Require(sel != 254 || !undefinedPrevious.Contains(chan), "PV read after an operation without a result");
        Require(sel != 255 || !undefinedPrevious.Contains(4), "PS read after an operation without a result");
        string value;
        if (sel < 128) value = $"r[{sel}].{Channel(chan)}";
        else if (sel < 192)
        {
            int bank = sel < 160 ? 0 : 1;
            int binding = Bits(cf0, 22 + bank * 4, 4);
            int mode = bank == 0 ? Bits(cf0, 30, 2) : Bits(cf1, 0, 2);
            Require(mode is 1 or 2, "unsupported constant-cache mode");
            int element = Bits(cf1, 2 + bank * 8, 8) * 16 + sel - (bank == 0 ? 128 : 160);
            Require(mode == 2 || sel - (bank == 0 ? 128 : 160) < 16, "constant-cache access exceeds lock");
            var matches = UniformBlocks.Where(x => x.Offset == binding).ToArray();
            Require(matches.Length == 1, $"missing/duplicate uniform binding {binding}");
            var buffer = matches[0];
            Require((long)element * 16 + chan * 4 + 4 <= buffer.Size, $"uniform access outside {buffer.Name}");
            buffers[binding] = new(buffer.Name, buffer.Offset, buffer.Size);
            value = $"gx2_cb{binding}.data[{element}].{Channel(chan)}";
        }
        else value = sel switch
        {
            248 => "0u", 249 => "0x3F800000u", 250 => "1u", 251 => "0xFFFFFFFFu", 252 => "0x3F000000u",
            253 => Hex(literals[chan]), 254 => $"pv.{Channel(chan)}", 255 => "ps",
            _ => throw Unsupported($"ALU source selector {sel}")
        };
        bool abs = !inst.Op3 && index < 2 && (inst.B & (1u << index)) != 0;
        bool neg = Bits(word, shift + 12, 1) != 0;
        Require(!integer || (!abs && !neg), "integer source modifiers");
        if (abs || neg)
        {
            string f = $"uintBitsToFloat({value})";
            if (abs) f = $"abs({f})";
            if (neg) f = $"(-{f})";
            value = $"floatBitsToUint({f})";
        }
        return value;
    }

    private string Evaluate(Alu i, uint cf0, uint cf1, uint[] literals)
    {
        string U(int n) => Source(i, n, cf0, cf1, literals);
        string I(int n) => Source(i, n, cf0, cf1, literals, true);
        string F(int n) => $"uintBitsToFloat({U(n)})";
        string Float(string expression) => $"floatBitsToUint({expression})";
        bool integerOutput = !i.Op3 && (i.Op is >= 0x30 and <= 0x3F or >= 0x70 and <= 0x75 or 0x6B or 0x79 or >= 0x0C and <= 0x0F);
        Require(!integerOutput || (Bits(i.B, 5, 2) == 0 && (i.B & 0x80000000) == 0), "integer result modifiers");
        if (!i.Op3 && i.Op is 0x65 or 0x68)
        {
            usesFiniteReciprocal = true;
            return Float($"gx2_flush_inf(1.0 / {(i.Op == 0x68 ? $"sqrt({F(0)})" : F(0))})");
        }
        if (i.Op3)
        {
            return i.Op switch
            {
                0x10 => Float($"gx2_mul({F(0)}, {F(1)}) + {F(2)}"),
                0x11 => Float($"(gx2_mul({F(0)}, {F(1)}) + {F(2)}) * 2.0"),
                0x12 => Float($"(gx2_mul({F(0)}, {F(1)}) + {F(2)}) * 4.0"),
                0x13 => Float($"(gx2_mul({F(0)}, {F(1)}) + {F(2)}) * 0.5"),
                0x14 => Float($"{F(0)} * {F(1)} + {F(2)}"),
                0x18 => $"({F(0)} == 0.0 ? {U(1)} : {U(2)})",
                0x19 => $"({F(0)} > 0.0 ? {U(1)} : {U(2)})",
                0x1A => $"({F(0)} >= 0.0 ? {U(1)} : {U(2)})",
                0x1C => $"({I(0)} == 0u ? {U(1)} : {U(2)})",
                0x1D => $"(int({I(0)}) > 0 ? {U(1)} : {U(2)})",
                0x1E => $"(int({I(0)}) >= 0 ? {U(1)} : {U(2)})",
                _ => throw Unsupported($"ALU OP3 {i.Op:X}")
            };
        }
        return i.Op switch
        {
            0x00 => Float($"{F(0)} + {F(1)}"),
            0x01 => Float($"gx2_mul({F(0)}, {F(1)})"),
            0x02 => Float($"{F(0)} * {F(1)}"),
            0x03 or 0x05 => Float($"max({F(0)}, {F(1)})"),
            0x04 or 0x06 => Float($"min({F(0)}, {F(1)})"),
            0x08 => Float($"({F(0)} == {F(1)} ? 1.0 : 0.0)"),
            0x09 => Float($"({F(0)} > {F(1)} ? 1.0 : 0.0)"),
            0x0A => Float($"({F(0)} >= {F(1)} ? 1.0 : 0.0)"),
            0x0B => Float($"({F(0)} != {F(1)} ? 1.0 : 0.0)"),
            0x0C => $"({F(0)} == {F(1)} ? 0xFFFFFFFFu : 0u)",
            0x0D => $"({F(0)} > {F(1)} ? 0xFFFFFFFFu : 0u)",
            0x0E => $"({F(0)} >= {F(1)} ? 0xFFFFFFFFu : 0u)",
            0x0F => $"({F(0)} != {F(1)} ? 0xFFFFFFFFu : 0u)",
            0x10 => Float($"fract({F(0)})"),
            0x11 => Float($"trunc({F(0)})"),
            0x13 => Float($"roundEven({F(0)})"),
            0x14 => Float($"floor({F(0)})"),
            0x19 => U(0),
            0x30 => $"({I(0)} & {I(1)})",
            0x31 => $"({I(0)} | {I(1)})",
            0x32 => $"({I(0)} ^ {I(1)})",
            0x33 => $"(~{I(0)})",
            0x34 => $"({I(0)} + {I(1)})",
            0x35 => $"({I(0)} - {I(1)})",
            0x36 => $"uint(max(int({I(0)}), int({I(1)})))",
            0x37 => $"uint(min(int({I(0)}), int({I(1)})))",
            0x38 => $"max({I(0)}, {I(1)})",
            0x39 => $"min({I(0)}, {I(1)})",
            0x3A => $"({I(0)} == {I(1)} ? 0xFFFFFFFFu : 0u)",
            0x3B => $"(int({I(0)}) > int({I(1)}) ? 0xFFFFFFFFu : 0u)",
            0x3C => $"(int({I(0)}) >= int({I(1)}) ? 0xFFFFFFFFu : 0u)",
            0x3D => $"({I(0)} != {I(1)} ? 0xFFFFFFFFu : 0u)",
            0x3E => $"({I(0)} > {I(1)} ? 0xFFFFFFFFu : 0u)",
            0x3F => $"({I(0)} >= {I(1)} ? 0xFFFFFFFFu : 0u)",
            0x61 => Float($"exp2({F(0)})"),
            0x62 => Float($"gx2_log({F(0)})"),
            0x63 => Float($"log2(max(0.0, {F(0)}))"),
            0x66 => Float($"1.0 / {F(0)}"),
            0x69 => Float($"1.0 / sqrt({F(0)})"),
            0x6A => Float($"sqrt({F(0)})"),
            0x6B => $"uint(int({F(0)}))",
            0x6C => Float($"float(int({I(0)}))"),
            0x6D => Float($"float({I(0)})"),
            0x6E => Float($"sin({F(0)} / 0.1591549367)"),
            0x6F => Float($"cos({F(0)} / 0.1591549367)"),
            0x70 => $"uint(int({I(0)}) >> ({I(1)} & 31u))",
            0x71 => $"({I(0)} >> ({I(1)} & 31u))",
            0x72 => $"({I(0)} << ({I(1)} & 31u))",
            0x73 or 0x75 => $"({I(0)} * {I(1)})",
            _ => throw Unsupported($"ALU OP2 {i.Op:X}")
        };
    }
}
