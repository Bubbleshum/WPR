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

namespace Microsoft.Xna.Framework.Graphics
{
	public class OcclusionQuery : GraphicsResource
	{
		#region Public Properties

		public bool IsComplete
		{
			get
			{
				return XnaBackend.Graphics.QueryComplete(
					GraphicsDevice.GLDevice,
					query
				) == 1;
			}
		}

		public int PixelCount
		{
			get
			{
				return XnaBackend.Graphics.QueryPixelCount(
					GraphicsDevice.GLDevice,
					query
				);
			}
		}

		#endregion

		#region Private FNA3D Variables

		private IntPtr query;

		#endregion

		#region Public Constructor

		public OcclusionQuery(GraphicsDevice graphicsDevice)
		{
			GraphicsDevice = graphicsDevice;
			query = XnaBackend.Graphics.CreateQuery(GraphicsDevice.GLDevice);
		}

		#endregion

		#region Destructor

		~OcclusionQuery()
		{
			Dispose(false);
		}

		#endregion


		#region Protected Dispose Method

		protected override void Dispose(bool disposing)
		{
			if (!IsDisposed)
			{
				XnaBackend.Graphics.AddDisposeQuery(GraphicsDevice.GLDevice, query);
			}
			base.Dispose(disposing);
		}

		#endregion

		#region Public Begin/End Methods

		public void Begin()
		{
			XnaBackend.Graphics.QueryBegin(GraphicsDevice.GLDevice, query);
		}

		public void End()
		{
			XnaBackend.Graphics.QueryEnd(GraphicsDevice.GLDevice, query);
		}

		#endregion
	}
}
