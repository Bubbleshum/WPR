namespace WPR.Wp8Native
{
    public static partial class WinRtSystemInterfaces
    {
        /// <summary>A type's ABI shape: I interface, C runtime class, D delegate, E enum, S struct or primitive.</summary>
        public sealed record TypeShape(char Kind, int Size, int Align, int Hfa)
        {
            /// <summary>Travels as an object pointer, so the honest stand-in for an unknown one is a placeholder object.</summary>
            public bool IsObject => Kind is 'I' or 'C' or 'D';
        }

        private static readonly Dictionary<string, TypeShape> PrimitiveShapes = new(StringComparer.Ordinal)
        {
            ["Boolean"] = new('S', 1, 1, 0),
            ["Byte"] = new('S', 1, 1, 0),
            ["SByte"] = new('S', 1, 1, 0),
            ["Char"] = new('S', 2, 2, 0),
            ["Int16"] = new('S', 2, 2, 0),
            ["UInt16"] = new('S', 2, 2, 0),
            ["Int32"] = new('S', 4, 4, 0),
            ["UInt32"] = new('S', 4, 4, 0),
            ["Int64"] = new('S', 8, 8, 0),
            ["UInt64"] = new('S', 8, 8, 0),
            ["Single"] = new('S', 4, 4, 1),
            ["Double"] = new('S', 8, 8, 1),
            ["String"] = new('S', 4, 4, 0),   // HSTRING; null is the empty string
            ["Object"] = new('I', 4, 4, 0),
        };

        private static readonly Lazy<Dictionary<string, TypeShape>> Shapes = new(() =>
            TypeTable.ToDictionary(t => t.Name, t => new TypeShape(t.Kind, t.Size, t.Align, t.Hfa), StringComparer.Ordinal));

        /// <summary>The shape of a type as a signature prints it, ignoring generic arguments; null when unknown (a bare generic parameter, a type outside the table).</summary>
        public static TypeShape? ShapeOf(string type)
        {
            int angle = type.IndexOf('<');
            string name = angle > 0 ? type[..angle] : type;
            return PrimitiveShapes.GetValueOrDefault(name) ?? Shapes.Value.GetValueOrDefault(name);
        }

        /// <summary>One argument or out-pointer: which core-register-or-stack word it starts at (r0 is <c>this</c>), or -1 when it travels in VFP registers.</summary>
        public sealed record AbiArgument(string Type, TypeShape? Shape, int Word, bool IsOut, bool IsArray);

        /// <summary>
        /// Where a method's arguments live, by the ARM hard-float procedure call standard WP8 uses.
        /// <see cref="Problem"/> is set when the layout cannot be computed honestly - a parameter
        /// of unknown size before the out-pointer means every later word is a guess.
        /// </summary>
        public sealed record AbiLayout(IReadOnlyList<AbiArgument> Parameters, AbiArgument? Result, string? Problem)
        {
            /// <summary>Every out-parameter, the return value's pointer last.</summary>
            public IEnumerable<AbiArgument> Outs => Parameters.Where(p => p.IsOut).Concat(Result is null ? [] : [Result]);
        }

        /// <summary>The ABI layout of <paramref name="method"/>.</summary>
        /// <remarks>
        /// Every WinRT method returns an HRESULT in r0 and takes <c>this</c> in r0; a non-void
        /// result is a trailing out-pointer. The two rules that make this more than counting
        /// words are both AAPCS-VFP, and both were measured on these images (<c>sinf</c> takes its
        /// angle in s0): Single, Double and structs of up to four of only one of them (Rect, Point,
        /// Size) go in s/d registers and use no core word; and a 64-bit value starts at an even
        /// word.
        /// </remarks>
        public static AbiLayout Layout(Method method)
        {
            (string[] parameters, string result) = Parse(method.Signature);
            List<AbiArgument> laid = new(parameters.Length);
            int word = 1;
            string? problem = null;

            foreach (string parameter in parameters)
            {
                if (parameter.EndsWith("[]&", StringComparison.Ordinal) || parameter.EndsWith("[]", StringComparison.Ordinal))
                {
                    // A WinRT array is (UInt32 length, T* data); an out array is (UInt32*, T**).
                    bool isOut = parameter.EndsWith('&');
                    string element = parameter[..parameter.IndexOf("[]", StringComparison.Ordinal)];
                    laid.Add(new AbiArgument(element, ShapeOf(element), word, isOut, IsArray: true));
                    word += 2;
                    continue;
                }

                if (parameter.EndsWith('&'))
                {
                    string target = parameter[..^1];
                    laid.Add(new AbiArgument(target, ShapeOf(target), word, IsOut: true, IsArray: false));
                    word++;
                    continue;
                }

                TypeShape? shape = ShapeOf(parameter);
                if (shape is null)
                {
                    problem ??= $"parameter type {parameter} has no known size";
                    laid.Add(new AbiArgument(parameter, null, word, IsOut: false, IsArray: false));
                    word++;
                    continue;
                }

                if (shape.Hfa > 0)
                {
                    laid.Add(new AbiArgument(parameter, shape, -1, IsOut: false, IsArray: false));
                    continue;
                }

                int words = (shape.Size + 3) / 4;
                if (shape.Align == 8 && (word & 1) == 1)
                {
                    word++;
                }

                // No split rule is needed: after even-word rounding an 8-byte value cannot straddle
                // r3, and a larger composite may legally be split between r3 and the stack (C.5),
                // which the linear word numbering already models.

                laid.Add(new AbiArgument(parameter, shape, word, IsOut: false, IsArray: false));
                word += words;
            }

            AbiArgument? returned = result == "Void"
                ? null
                : new AbiArgument(result, ShapeOf(result), word, IsOut: true, IsArray: false);

            return new AbiLayout(laid, returned, problem);
        }

        /// <summary>"Name(A, B) : R" into its parameter and result types. Generic arguments are joined without a space, so ", " splits parameters only.</summary>
        private static (string[] Parameters, string Result) Parse(string signature)
        {
            int open = signature.IndexOf('(');
            int close = signature.LastIndexOf(") : ", StringComparison.Ordinal);
            string inner = signature[(open + 1)..close];
            string result = signature[(close + 4)..];
            return (inner.Length == 0 ? [] : inner.Split(", "), result);
        }
    }
}
