using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// Vertex programs have their position's depth pulled a hair inside the clip volume, because a
/// vertex at exactly z = 0 or z = w is drawn by Direct3D and clipped by OpenGL once MojoShader maps
/// the depth (z * 2 - w). GTA: San Andreas draws its menus at z = w, and on OpenGL only their text
/// was left.
/// </summary>
public sealed class DepthInsetSuite : IContractSuite
{
    public string Name => "depth inset";

    // Shader model 2 tokens: register type in bits 28-30 (and 11-12), number in 0-10, swizzle or
    // write mask in 16-23.
    private const uint V0 = 0x90E40000, OPos = 0xC00F0000, OPosXY = 0xC0030000, Version = 0xFFFE0200, End = 0x0000FFFF;

    private static uint TempDst(int register) => 0x800F0000u | (uint)register;
    private static uint TempSrc(int register) => 0x80E40000u | (uint)register;

    private static byte[] Program(params uint[] body) => new[] { Version }.Concat(body).Append(End).SelectMany(BitConverter.GetBytes).ToArray();

    private static uint[] Tokens(byte[] code) => Enumerable.Range(0, code.Length / 4).Select(i => BitConverter.ToUInt32(code, i * 4)).ToArray();

    public void Run(Harness h)
    {
        // mov oPos, v0 with two constants mapped (c0-c1).
        uint[] tokens = Tokens(Aon9Shader.KeepDepthInsideClipVolume(Program(0x02000001, OPos, V0), constantRegisters: 2));
        h.Check("a def above the mapped constants comes straight after the version token",
            tokens[1] == (81u | (5u << 24)) && tokens[2] == 0xA00F0002u, $"{tokens[1]:X8} {tokens[2]:X8}");
        h.Check("... holding 1 - 2e and e",
            BitConverter.UInt32BitsToSingle(tokens[3]) == 1f - (2f * Aon9Shader.DepthInset) && BitConverter.UInt32BitsToSingle(tokens[4]) == Aon9Shader.DepthInset);
        h.Check("the game's write to oPos goes to the first spare temp instead", tokens[7] == 0x02000001 && tokens[8] == TempDst(0), $"{tokens[7]:X8} {tokens[8]:X8}");
        h.Check("the program ends by copying that temp to oPos", tokens[^4] == 0x02000001 && tokens[^3] == OPos && tokens[^2] == TempSrc(0) && tokens[^1] == End,
            string.Join(" ", tokens[^4..].Select(t => t.ToString("X8"))));

        // A program using r0 already gets r1.
        tokens = Tokens(Aon9Shader.KeepDepthInsideClipVolume(Program(0x02000001, TempDst(0), V0, 0x02000001, OPos, TempSrc(0)), constantRegisters: 0));
        h.Check("the spare temp is above every temp the program uses", tokens[^2] == TempSrc(1), tokens[^2].ToString("X8"));

        byte[] flat = Program(0x02000001, OPosXY, V0);
        h.Check("a program that writes no depth is left alone", Aon9Shader.KeepDepthInsideClipVolume(flat, 0).SequenceEqual(flat));

        byte[] full = Program(0x02000001, TempDst(11), V0, 0x02000001, OPos, TempSrc(11));
        h.Check("a program using r11 (no spare temp in vs_2_0) is left alone", Aon9Shader.KeepDepthInsideClipVolume(full, 0).SequenceEqual(full));

        // The arithmetic the tail does, at both edges of Direct3D's range (w = 1).
        float a = 1f - (2f * Aon9Shader.DepthInset), e = Aon9Shader.DepthInset;
        float far = (1f * a) + (1f * e), near = (0f * a) + (1f * e);
        h.Check("z = w lands inside the far plane", far < 1f && far > 0.999f, far.ToString("R"));
        h.Check("z = 0 lands inside the near plane", near > 0f && near < 0.001f, near.ToString("R"));
    }
}
