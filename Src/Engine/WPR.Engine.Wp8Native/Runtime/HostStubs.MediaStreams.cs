namespace WPR.Wp8Native
{
    /// <summary>
    /// The host's streams as Media Foundation sees them (<see cref="IHostStreams"/>): the WinRT
    /// random-access streams of <see cref="WinRtRuntime"/> and the HGLOBAL IStreams of
    /// <c>CreateStreamOnHGlobal</c>.
    /// </summary>
    public sealed partial class HostStubs
    {
        private sealed class MediaStreams(HostStubs stubs) : IHostStreams
        {
            public HostStreamBinding? Bind(long pointer)
            {
                if (stubs._winRt.BindStream(pointer) is { } winRt)
                {
                    return winRt;
                }

                if (stubs.HostObjectFor(pointer) is not { State: StreamState s } owner)
                {
                    return null;
                }

                owner.References++;
                bool released = false;
                return new HostStreamBinding
                {
                    Data = new GuestBlockStream(stubs, s.Block),
                    GetPosition = () => s.Position,
                    SetPosition = p => s.Position = p,
                    CanRead = true,
                    CanWrite = true,
                    Source = owner.Address,
                    Release = () =>
                    {
                        if (!released)
                        {
                            released = true;
                            if (--owner.References == 0)
                            {
                                owner.Class.OnFinalRelease?.Invoke(owner);
                            }
                        }
                    },
                };
            }

            public long Expose(HostStreamBinding binding, Guid iid)
            {
                // An HGLOBAL stream asked for as IStream is itself: it is what the byte stream wraps,
                // and its seek pointer is the byte stream's position already.
                if (stubs.HostObjectAt(binding.Source) is { State: StreamState } stream &&
                    (iid == MediaFoundation.IidStream || iid == MediaFoundation.IidSequentialStream || iid == IidUnknown))
                {
                    stream.References++;
                    return stream.Address;
                }

                return stubs._winRt.StreamOver(binding, iid);
            }
        }

        /// <summary>An HGLOBAL stream's memory as a .NET stream. Its position is ignored by the bindings, which set it before every transfer.</summary>
        private sealed class GuestBlockStream(HostStubs stubs, HGlobalBlock block) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => true;

            public override long Length => block.Size;

            public override long Position { get; set; }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = (int)Math.Clamp(block.Size - Position, 0, count);
                if (n > 0)
                {
                    stubs._emulator.ReadMemory(block.Address + Position, n).CopyTo(buffer, offset);
                    Position += n;
                }

                return n;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                long end = Position + count;
                if (end > CallFrame.SaneLengthLimit || !stubs.EnsureCapacity(block, end))
                {
                    throw new IOException("the HGLOBAL stream could not grow");
                }

                if (Position > block.Size)
                {
                    stubs._emulator.WriteMemory(block.Address + block.Size, new byte[Position - block.Size]);
                }

                stubs._emulator.WriteMemory(block.Address + Position, buffer.AsSpan(offset, count).ToArray());
                Position = end;
                block.Size = Math.Max(block.Size, end);
            }

            public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => block.Size + offset,
            };

            public override void SetLength(long value)
            {
                if (value < 0 || value > CallFrame.SaneLengthLimit || !stubs.EnsureCapacity(block, value))
                {
                    throw new IOException("the HGLOBAL stream could not grow");
                }

                if (value > block.Size)
                {
                    stubs._emulator.WriteMemory(block.Address + block.Size, new byte[value - block.Size]);
                }

                block.Size = value;
            }
        }
    }
}
