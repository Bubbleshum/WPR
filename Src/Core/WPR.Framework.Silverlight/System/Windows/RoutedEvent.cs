using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.RoutedEvent</c>.</summary>
    /// <remarks>
    /// The identifiers <c>UIElement.KeyDownEvent</c>, <c>MouseLeftButtonDownEvent</c> and the rest,
    /// handed to <see cref="UIElement.AddHandler"/>. Each names the CLR event it stands for, and
    /// <c>AddHandler</c> subscribes to that event, so a handler added either way is raised by the
    /// same code. Coding4Fun's LoopingSelector and Microsoft Bingo's ButtonStateManager add theirs
    /// this way; without the type their constructors did not load.
    /// </remarks>
    public sealed class RoutedEvent
    {
        internal RoutedEvent(string name, Type ownerType)
        {
            Name = name;
            OwnerType = ownerType;
        }

        /// <summary>The CLR event this identifier stands for.</summary>
        internal string Name { get; }

        internal Type OwnerType { get; }

        public override string ToString() => OwnerType.Name + "." + Name;
    }
}
