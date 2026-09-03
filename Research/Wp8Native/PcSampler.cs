namespace WPR.Wp8Native
{
    /// <summary>
    /// Says where an image is spending its time, by address and by function.
    /// </summary>
    /// <remarks>
    /// The runaway detector answers "is it looping?" and answers it only for a loop that
    /// never calls out. That leaves the case this was written for uncovered: Angry Birds Rio
    /// sits on its LOADING screen with nothing outstanding - no wait, no queued work, no file
    /// still being read - running a string-comparison loop that calls <c>memcmp</c> every few
    /// instructions. It calls out constantly, so the runaway detector is silent, and the
    /// import counts say only that <c>memcmp</c> was called 7.8 million times, which is not a
    /// place in the program.
    /// <para>
    /// Two sources, because they answer different questions and cost very different amounts:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Call sites</b> are free. Every trap already knows the caller's address - it
    /// is in <c>lr</c> - so recording it turns "memcmp was called 7.8 million times" into
    /// "from this address, in this function". Always on.</item>
    /// <item><b>Blocks</b> need a block hook, which is not free, so this is opt-in through
    /// <c>WPR_SAMPLE=n</c>. It sees code that calls nothing at all, which call sites cannot.
    /// </item>
    /// </list>
    /// <para>
    /// Neither is a sampling profiler in the usual sense - there is no timer here and no
    /// second thread that could safely read the CPU - so these count events rather than
    /// elapsed time. For finding a loop that is running when it should not be, which is what
    /// this is for, a count is the better measure anyway: it is exact, and it is the same on
    /// every run.
    /// </para>
    /// </remarks>
    public sealed class PcSampler
    {
        /// <summary>Sample one block in this many, from <c>WPR_SAMPLE</c>; zero is off.</summary>
        public static readonly int BlockStride =
            int.TryParse(Environment.GetEnvironmentVariable("WPR_SAMPLE"), out int every) && every > 0
                ? every
                : 0;

        /// <summary>Whether block sampling was asked for.</summary>
        public static bool SamplingBlocks => BlockStride > 0;

        /// <summary>
        /// A call site to record arguments for, from <c>WPR_ARGS=0xADDR</c>.
        /// </summary>
        /// <remarks>
        /// Knowing that one address accounts for 27% of every call across the boundary says
        /// where the loop is. It does not say what the loop is *for*, and for a
        /// string-comparison loop that is the whole question: comparing what against what, and
        /// how many times before it gives up. The arguments answer it in a line.
        /// </remarks>
        public static readonly long ArgumentSite =
            Environment.GetEnvironmentVariable("WPR_ARGS") is { Length: > 0 } text &&
            long.TryParse(
                text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out long parsed)
                ? parsed
                : 0;

        /// <summary>How many argument samples to keep.</summary>
        private const int ArgumentWindow = 16;

        private readonly Queue<string> _arguments = new();

        private long _argumentCalls;

        /// <summary>
        /// The last few argument samples from <see cref="ArgumentSite"/>, and how many there
        /// were in total.
        /// </summary>
        /// <remarks>
        /// The *last* few, not the first. The first are always startup, and startup is the
        /// part that worked - what is wanted is what the loop is doing now, after a million
        /// iterations, which is a different question and often a different answer.
        /// </remarks>
        public IEnumerable<string> ArgumentSamples =>
            _argumentCalls == 0
                ? []
                : _arguments.Prepend(
                    $"last {_arguments.Count} of {_argumentCalls:N0} call(s) from " +
                    $"0x{ArgumentSite:X8}:");

        /// <summary>Records one argument sample, keeping only the most recent.</summary>
        public void RecordArguments(string description)
        {
            _argumentCalls++;
            _arguments.Enqueue(description);
            if (_arguments.Count > ArgumentWindow)
            {
                _arguments.Dequeue();
            }
        }

        private readonly Dictionary<long, int> _callSites = new();
        private readonly Dictionary<long, string> _lastImport = new();
        private readonly Dictionary<long, int> _blocks = new();
        private long _blocksSeen;

        public long CallSiteSamples { get; private set; }

        public long BlockSamples { get; private set; }

        /// <summary>
        /// Records that <paramref name="import"/> was entered from <paramref name="caller"/>.
        /// </summary>
        public void RecordCallSite(long caller, string import)
        {
            if (caller == 0)
            {
                return;
            }

            // Thumb return addresses carry the low bit. Clearing it keeps one call site from
            // appearing as two, which it otherwise does whenever a function is entered both
            // ways.
            caller &= ~1L;

            CallSiteSamples++;
            _callSites[caller] = _callSites.GetValueOrDefault(caller) + 1;
            _lastImport[caller] = import;
        }

        /// <summary>Records a basic block, sampled at the configured stride.</summary>
        public void RecordBlock(long address)
        {
            if (++_blocksSeen % BlockStride != 0)
            {
                return;
            }

            BlockSamples++;
            _blocks[address] = _blocks.GetValueOrDefault(address) + 1;
        }

        /// <summary>
        /// The report: hottest call sites, hottest blocks, and the functions they fall in.
        /// </summary>
        public IEnumerable<string> Report(ArmUnwinder unwinder, int take = 12)
        {
            if (CallSiteSamples == 0 && BlockSamples == 0)
            {
                yield return "no samples";
                yield break;
            }

            if (CallSiteSamples > 0)
            {
                yield return $"{CallSiteSamples:N0} call(s) across the boundary from " +
                             $"{_callSites.Count:N0} distinct site(s)";

                foreach ((long site, int count) in _callSites.OrderByDescending(e => e.Value).Take(take))
                {
                    yield return $"  {count * 100.0 / CallSiteSamples,5:F1}%  {count,12:N0}  " +
                                 $"0x{site:X8} {Where(unwinder, site)} -> {_lastImport[site]}";
                }

                foreach (string line in ByFunction(unwinder, _callSites, CallSiteSamples, "call site"))
                {
                    yield return line;
                }
            }

            if (BlockSamples == 0)
            {
                yield break;
            }

            yield return $"{BlockSamples:N0} block sample(s) at 1-in-{BlockStride:N0} from " +
                         $"{_blocks.Count:N0} distinct block(s)";

            foreach ((long block, int count) in _blocks.OrderByDescending(e => e.Value).Take(take))
            {
                yield return $"  {count * 100.0 / BlockSamples,5:F1}%  {count,12:N0}  " +
                             $"0x{block:X8} {Where(unwinder, block)}";
            }

            foreach (string line in ByFunction(unwinder, _blocks, BlockSamples, "block"))
            {
                yield return line;
            }
        }

        /// <summary>
        /// Rolls addresses up to the functions containing them.
        /// </summary>
        /// <remarks>
        /// A loop is several blocks and several call sites, so the per-address list can spread
        /// one hot loop across a dozen rows and bury it under something that is merely
        /// frequent. Summed by function it comes back together.
        /// </remarks>
        private static IEnumerable<string> ByFunction(
            ArmUnwinder unwinder, Dictionary<long, int> counts, long total, string unit)
        {
            var byFunction = new Dictionary<long, (int Count, int Sites)>();
            foreach ((long address, int count) in counts)
            {
                long function = unwinder.FunctionStart(address);
                (int previous, int sites) = byFunction.GetValueOrDefault(function);
                byFunction[function] = (previous + count, sites + 1);
            }

            yield return $"by function ({byFunction.Count:N0} distinct):";
            foreach ((long function, (int count, int sites)) in
                     byFunction.OrderByDescending(e => e.Value.Count).Take(6))
            {
                string name = function == 0 ? "(outside the image)" : $"0x{function:X8}";
                yield return $"  {count * 100.0 / total,5:F1}%  {count,12:N0}  {name} " +
                             $"across {sites} {unit}(s)";
            }
        }

        private static string Where(ArmUnwinder unwinder, long address)
        {
            long function = unwinder.FunctionStart(address);
            return function == 0
                ? "(no .pdata entry)"
                : $"in 0x{function:X8}+0x{address - function:X}";
        }
    }
}
