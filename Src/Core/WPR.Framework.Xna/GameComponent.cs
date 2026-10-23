#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
#endregion

namespace Microsoft.Xna.Framework
{
	public class GameComponent : IGameComponent, IUpdateable, IComparable<GameComponent>, IDisposable
	{
		#region Public Properties

		public Game Game
		{
			get;
			private set;
		}

		public bool Enabled
		{
			get
			{
				return _enabled;
			}
			set
			{
				if (_enabled != value)
				{
					_enabled = value;
					if (this.EnabledChanged != null)
					{
						this.EnabledChanged(this, EventArgs.Empty);
					}
					OnEnabledChanged(this, null);
				}
			}
		}

		public int UpdateOrder
		{
			get
			{
				return _updateOrder;
			}
			set
			{
				if (_updateOrder != value)
				{
					_updateOrder = value;
					if (this.UpdateOrderChanged != null)
					{
						this.UpdateOrderChanged(this, EventArgs.Empty);
					}
					OnUpdateOrderChanged(this, null);
				}
			}
		}

		#endregion

		#region Private Variables

		bool _enabled = true;
		int _updateOrder;
		bool _disposing;

		#endregion

		#region Events

		public event EventHandler<EventArgs> Disposed;
		public event EventHandler<EventArgs> EnabledChanged;
		public event EventHandler<EventArgs> UpdateOrderChanged;

		#endregion

		#region Public Constructors

		public GameComponent(Game game)
		{
			this.Game = game;
		}

		#endregion

		#region Destructor

		~GameComponent()
		{
			Dispose(false);
		}

		#endregion

		#region Public Dispose Method

		/// <summary>
		/// Shuts down the component.
		/// </summary>
		public void Dispose()
		{
			// WPR: a Dispose that reaches this component's own Dispose() again returns. The
			// Microsoft Ad SDK's AdComponentCommon.Dispose calls Dispose() on the AdGameComponent
			// that is disposing it, so Game.Dispose (which disposes every component, as XNA's did)
			// recursed until the stack overflowed and took the whole launcher down (iRunner, at
			// teardown). On the phone the shell ended the process without disposing the game.
			if (_disposing)
			{
				return;
			}

			_disposing = true;
			try
			{
				Dispose(true);
			}
			finally
			{
				_disposing = false;
			}

			GC.SuppressFinalize(this);
		}

		#endregion

		#region Public Virtual Methods

		public virtual void Initialize() {}

		public virtual void Update(GameTime gameTime) {}

		#endregion

		#region Protected Virtual Methods

		protected virtual void OnUpdateOrderChanged(object sender, EventArgs args) {}

		protected virtual void OnEnabledChanged(object sender, EventArgs args) {}

		/// <summary>
		/// Shuts down the component.
		/// </summary>
		protected virtual void Dispose(bool disposing)
		{
			if (disposing && Disposed != null)
			{
				Disposed(this, EventArgs.Empty);
			}
		}

		#endregion

		#region IComparable<GameComponent> Members

		int IComparable<GameComponent>.CompareTo(GameComponent other)
		{
			return other.UpdateOrder - this.UpdateOrder;
		}

		#endregion
	}
}
