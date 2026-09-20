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
using System.Collections.Generic;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{	
	public abstract class GraphicsResource : IDisposable
	{
		#region Public Properties

		public GraphicsDevice GraphicsDevice
		{
			get
			{
				return graphicsDevice;
			}
			internal set
			{
				if (graphicsDevice == value)
				{
					return;
				}

				/* VertexDeclaration objects can be bound to
				 * multiple GraphicsDevice objects during their
				 * lifetime. But only one GraphicsDevice should
				 * retain ownership.
				 */
				if (graphicsDevice != null && selfReference != null)
				{
					graphicsDevice.RemoveResourceReference(selfReference);
					selfReference = null;
				}

				graphicsDevice = value;

				selfReference = new WeakReference(this);
				graphicsDevice.AddResourceReference(selfReference);
			}
		}

		public bool IsDisposed
		{
			get;
			private set;
		}

		public string Name
		{
			get;
			set;
		}

		public Object Tag
		{
			get;
			set;
		}

		#endregion

		#region Private Variables

		private WeakReference selfReference;

		private GraphicsDevice graphicsDevice;

		#endregion

		#region Internal State-Object Binding

		/// <summary>
		/// Marks this resource as belonging to <paramref name="device"/>, the way XNA binds a state
		/// object the first time it is assigned to a GraphicsDevice.
		/// </summary>
		/// <remarks>
		/// <para>This exists because games branch on <c>GraphicsResource.GraphicsDevice</c> to decide
		/// whether a state object is safe to mutate. XNA's rule is that an unbound state object is
		/// mutable and a bound one is read-only, so the idiom is
		/// <c>if (state.GraphicsDevice != null) state = state.Clone();</c> before writing to it. FNA
		/// never bound state objects at all, so <c>GraphicsDevice</c> stayed null forever and that
		/// test always chose "mutate in place" - on the shared <c>BlendState.Opaque</c> /
		/// <c>DepthStencilState.Default</c> singletons every material starts out holding.</para>
		///
		/// <para><b>Kinectimals is the reference case.</b> Its material system
		/// (<c>EffectPropertySet.Apply</c>, driven by <c>Content/Core/EffectProperties.xml</c>) writes
		/// <c>DestinationBlend = InverseSourceAlpha</c> onto every material whose name contains
		/// "alpha", and a default rule writes <c>DestinationBlend = Zero</c> onto everything. With no
		/// clone, both rules landed on the same global object, so whichever model was applied last
		/// won. The damage is invisible on opaque geometry - at alpha 1, InverseSourceAlpha and Zero
		/// produce the same pixel - and shows only on alpha-blended parts, which then draw opaque.
		/// Its leaf textures are premultiplied DXT5 whose transparent region is exactly (0,0,0,0),
		/// so drawing them opaque paints solid black around every leaf. Identical on every driver,
		/// which is what rules out the renderer.</para>
		///
		/// <para>This deliberately does <b>not</b> go through the <see cref="GraphicsDevice"/> setter.
		/// That setter registers a resource reference, and the state singletons are process-lifetime
		/// while a device is per game launch - registering them would let the first game's device
		/// disposal take <c>BlendState.Opaque</c> down for every launch after it. Binding is also
		/// one-way and first-writer-wins, matching XNA, where a bound state object never changes
		/// owner.</para>
		/// </remarks>
		internal void BindToGraphicsDevice(GraphicsDevice device)
		{
			if (graphicsDevice == null)
			{
				graphicsDevice = device;
			}
		}

		#endregion

		#region Disposing Event

		public event EventHandler<EventArgs> Disposing;

		#endregion

		#region Internal Constructor and Destructor

		internal GraphicsResource()
		{
		}

		~GraphicsResource()
		{
			// FIXME: We really should call Dispose() here! -flibit
		}

		#endregion

		#region Public Dispose Method

		public void Dispose()
		{
			// Dispose of unmanaged objects as well
			Dispose(true);

			// Since we have been manually disposed, do not call the finalizer on this object
			GC.SuppressFinalize(this);
		}

		#endregion

		#region Public Methods

		public override string ToString()
		{
			return string.IsNullOrEmpty(Name) ? base.ToString() : Name;
		}

		#endregion

		#region Internal Methods

		/// <summary>
		/// Called before the device is reset. Allows graphics resources to
		/// invalidate their state so they can be recreated after the device reset.
		/// Warning: This may be called after a call to Dispose() up until
		/// the resource is garbage collected.
		/// </summary>
		internal protected virtual void GraphicsDeviceResetting()
		{
		}

		#endregion

		#region Protected Dispose Method

		/// <summary>
		/// The method that derived classes should override to implement disposing of
		/// managed and native resources.
		/// </summary>
		/// <param name="disposing">True if managed objects should be disposed.</param>
		/// <remarks>
		/// Native resources should always be released regardless of the value of the
		/// disposing parameter.
		/// </remarks>
		protected virtual void Dispose(bool disposing)
		{
			if (!IsDisposed)
			{
				// Do not trigger the event if called from the finalizer
				if (disposing && Disposing != null)
				{
					Disposing(this, EventArgs.Empty);
				}

				// Remove from the list of graphics resources
				if (graphicsDevice != null && selfReference != null)
				{
					graphicsDevice.RemoveResourceReference(selfReference);
					selfReference = null;
				}

				IsDisposed = true;
			}
		}

		#endregion
	}
}
