#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	public sealed class SamplerStateCollection
	{
		#region Public Array Access Property

		public SamplerState this[int index]
		{
			get
			{
				return samplers[index];
			}
			set
			{
				if (value != null)
				{
					/* XNA binds a state object to the device on assignment; see
					 * GraphicsResource.BindToGraphicsDevice for why that matters.
					 */
					value.BindToGraphicsDevice(device);
				}
				samplers[index] = value;
				modifiedSamplers[index] = true;
			}
		}

		#endregion

		#region Private Variables

		private readonly SamplerState[] samplers;
		private readonly bool[] modifiedSamplers;
		private readonly GraphicsDevice device;

		#endregion

		#region Internal Constructor

		internal SamplerStateCollection(
			GraphicsDevice graphicsDevice,
			int slots,
			bool[] modSamplers
		) {
			device = graphicsDevice;
			samplers = new SamplerState[slots];
			modifiedSamplers = modSamplers;
			for (int i = 0; i < samplers.Length; i += 1)
			{
				samplers[i] = SamplerState.LinearWrap;
			}
		}

		#endregion
	}
}
