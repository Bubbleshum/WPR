namespace WPR.Wp8Native
{
    /// <summary>
    /// The guest address space described the way VirtualQuery describes it.
    /// </summary>
    public sealed partial class ArmEmulator
    {
        public const uint MemCommit = 0x1000, MemFree = 0x10000;
        public const uint MemPrivate = 0x20000, MemImage = 0x1000000;
        public const uint PageNoAccess = 0x01, PageReadWrite = 0x04, PageExecuteRead = 0x20, PageExecuteReadWrite = 0x40;

        /// <summary>One VirtualQuery answer: a run of pages that share a state, protection and type.</summary>
        public readonly record struct MemoryRegion(long Base, long AllocationBase, long Size, uint State, uint Protect, uint AllocationProtect, uint Type);

        /// <summary>Heap blocks at least this big are reported as regions of their own (thread stacks, big buffers).</summary>
        private const long OwnRegionBytes = 64 * 1024;

        /// <summary>
        /// The region <paramref name="address"/> lies in. The image is one MEM_IMAGE allocation (it
        /// is mapped read-write-execute); the main stack, the trap page and the TEB are private
        /// allocations of their own; in the heap, a block of 64 KB or more (every guest thread's stack
        /// is one) is reported as its own allocation, and smaller blocks as the page they sit on.
        /// Anything else is MEM_FREE up to the next mapping.
        /// </summary>
        public MemoryRegion QueryRegion(long address)
        {
            long page = address & ~(PageSize - 1);
            long imageEnd = _image.ImageBase + Align(_image.SizeOfImage, PageSize);

            (long Start, long End, uint Protect, uint Type)[] fixedRegions =
            [
                (_image.ImageBase, imageEnd, PageExecuteReadWrite, MemImage),
                (StackBase, StackBase + StackSize, PageReadWrite, MemPrivate),
                (TrapBase, TrapBase + TrapSize, PageExecuteRead, MemPrivate),
                (TebBase, TebBase + TebSize, PageReadWrite, MemPrivate),
            ];

            foreach (var region in fixedRegions)
            {
                if (address >= region.Start && address < region.End)
                {
                    return new MemoryRegion(page, region.Start, region.End - page, MemCommit, region.Protect, region.Protect, region.Type);
                }
            }

            if (address >= HeapBase && address < HeapBase + HeapSize)
            {
                int block = BlockContaining(address);
                if (block >= 0 && _allocations[block].Size >= OwnRegionBytes && !_freed.Contains(_allocations[block].Start))
                {
                    long start = _allocations[block].Start & ~(PageSize - 1);
                    long end = Align(_allocations[block].Start + _allocations[block].Size, PageSize);
                    return new MemoryRegion(page, start, end - page, MemCommit, PageReadWrite, PageReadWrite, MemPrivate);
                }

                return new MemoryRegion(page, HeapBase, PageSize, MemCommit, PageReadWrite, PageReadWrite, MemPrivate);
            }

            // Free: up to the next mapping above, or the top of the 2 GB user space.
            long next = 0x80000000L;
            foreach (long start in new[] { _image.ImageBase, StackBase, HeapBase, TrapBase, TebBase })
            {
                if (start > page && start < next)
                {
                    next = start;
                }
            }

            if (next <= page)
            {
                next = page + PageSize;
            }

            return new MemoryRegion(page, 0, next - page, MemFree, PageNoAccess, 0, 0);
        }
    }
}
