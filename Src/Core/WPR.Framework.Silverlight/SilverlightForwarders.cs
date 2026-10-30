using System.Runtime.CompilerServices;

// Silverlight's System.Windows assembly also carried these, which .NET keeps in System.ObjectModel.
// The patcher points a game's System.Windows reference at this assembly, so it forwards them, as
// .NET's own System.Windows.dll facade does.
[assembly: TypeForwardedTo(typeof(System.Collections.ObjectModel.ObservableCollection<>))]
[assembly: TypeForwardedTo(typeof(System.Collections.ObjectModel.ReadOnlyObservableCollection<>))]
[assembly: TypeForwardedTo(typeof(System.Collections.Specialized.INotifyCollectionChanged))]
[assembly: TypeForwardedTo(typeof(System.Collections.Specialized.NotifyCollectionChangedAction))]
[assembly: TypeForwardedTo(typeof(System.Collections.Specialized.NotifyCollectionChangedEventArgs))]
[assembly: TypeForwardedTo(typeof(System.Collections.Specialized.NotifyCollectionChangedEventHandler))]
[assembly: TypeForwardedTo(typeof(System.ComponentModel.DataErrorsChangedEventArgs))]
[assembly: TypeForwardedTo(typeof(System.ComponentModel.INotifyDataErrorInfo))]
[assembly: TypeForwardedTo(typeof(System.Windows.Input.ICommand))]
