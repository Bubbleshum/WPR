namespace WPR.Wp8Native
{
    public sealed partial class HostStubs
    {
        /// <summary>MSVCP110's core (everything but iostreams and locale): see <see cref="MsvcpCore"/>.</summary>
        public MsvcpCore Msvcp { get; private set; } = null!;

        /// <summary>
        /// Wires <see cref="MsvcpCore"/> to the throw machinery: its <c>_X*</c> helpers and
        /// <c>rethrow_exception</c> throw through the same handler search a guest <c>throw</c> uses,
        /// and <c>current_exception</c> reads the exception the innermost running catch is handling.
        /// </summary>
        private void RegisterMsvcpCore()
        {
            Msvcp = new MsvcpCore(_emulator, _frame, ThrowIntoImage, CurrentlyHandled);
            Msvcp.RegisterInto(_handlers);
        }
    }
}
