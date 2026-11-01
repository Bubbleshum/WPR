using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// How a draw's vertices are copied out of emulated memory (<see cref="FrameCapture.Snapshot"/>).
/// N.O.V.A. 3 keeps a level's meshes in one large static vertex buffer and binds each submesh at
/// its own byte offset: copying each draw's range afresh every frame was ~300 MB/s of garbage and
/// pushed managed memory past 3 GB. Once a version's ranges cost more than the buffer, the whole
/// buffer is copied once and every stream of it is served from that copy at its own offset.
/// </summary>
public sealed class VertexSnapshotSuite : IContractSuite
{
    public string Name => "vertex snapshots";

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        const int size = 64 * 1024, stride = 52;
        long storage = h.Buffer(size);
        byte[] pattern = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        e.WriteMemory(storage, pattern);
        var buffer = new FrameCapture.Resource { Name = "vb", Storage = storage, StorageSize = size, Version = 1 };

        // Non-indexed draws of 200 vertices at byte offsets that are not multiples of the stride.
        FrameCapture.DrawCall Draw(int offset) => new(200, 0, 0, [new FrameCapture.VertexStream(buffer, stride, offset)], null, 0, 0, null, [], null, 4);
        int[] offsets = Enumerable.Range(0, 40).Select(i => 7 + (i * 1300)).Where(o => o + (200 * stride) <= size).ToArray();

        bool allRight = true;
        var arrays = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        for (int frame = 0; frame < 3; frame++)
        {
            foreach (int offset in offsets)
            {
                FrameCapture.VertexStream s = FrameCapture.Snapshot(e, Draw(offset)).Streams[0];
                if (s.Data is null)
                {
                    allRight = false;
                    continue;
                }

                arrays.Add(s.Data);
                // Vertex 0 and vertex 199's last byte read what the guest has at that address.
                int v0 = s.DataOffset + ((0 - s.FirstVertex) * stride);
                int v199 = s.DataOffset + ((199 - s.FirstVertex) * stride) + stride - 1;
                allRight &= s.Data[v0] == pattern[offset] && s.Data[v199] == pattern[offset + (199 * stride) + stride - 1];
            }
        }

        h.Check("every stream reads its own offset's vertices", allRight);
        h.Check("a static buffer drawn at many offsets is copied a handful of times, not per draw per frame",
            arrays.Count < offsets.Length, $"{arrays.Count} arrays for {offsets.Length} offsets x 3 frames");

        // One interleaved buffer bound in several slots at byte offsets inside the vertex, each slot
        // spanning every vertex (Asphalt 7: stride 44, offsets 12, 20 and 28). The last vertex of
        // such a stream runs past the buffer's end by its offset; it is still served from one copy.
        const int istride = 44, vertices = size / istride;
        var interleaved = new FrameCapture.Resource { Name = "interleaved", Storage = storage, StorageSize = vertices * istride, Version = 1 };
        FrameCapture.DrawCall Whole(int offset) => new(vertices, 0, 0, [new FrameCapture.VertexStream(interleaved, istride, offset)], null, 0, 0, null, [], null, 4);
        var interleavedArrays = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        bool interleavedRight = true;
        for (int frame = 0; frame < 3; frame++)
        {
            foreach (int offset in new[] { 12, 20, 28 })
            {
                FrameCapture.VertexStream s = FrameCapture.Snapshot(e, Whole(offset)).Streams[0];
                if (s.Data is null)
                {
                    interleavedRight = false;
                    continue;
                }

                interleavedArrays.Add(s.Data);
                int last = s.DataOffset + ((vertices - 1 - s.FirstVertex) * istride);
                interleavedRight &= s.Data[last] == pattern[offset + ((vertices - 1) * istride)];
            }
        }

        h.Check("streams bound at byte offsets into an interleaved buffer read their own vertices", interleavedRight);
        h.Check("an interleaved static buffer bound at byte offsets is copied once, not per draw",
            interleavedArrays.Count <= 2, $"{interleavedArrays.Count} arrays for 3 offsets x 3 frames");

        // A write (a new version) is seen: the old whole copy is not served.
        e.WriteMemory(storage + offsets[0], new byte[] { 0xEE });
        buffer.Version++;
        FrameCapture.VertexStream after = FrameCapture.Snapshot(e, Draw(offsets[0])).Streams[0];
        h.Check("a rewritten buffer is read again", after.Data is not null && after.Data[after.DataOffset - (after.FirstVertex * stride)] == 0xEE);
    }
}
