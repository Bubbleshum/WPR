using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Xna.Framework.GamerServices
{
    public class AvatarDescription
    {
        private static readonly byte[] EmptyDescription = Array.Empty<byte>();
        private AvatarBodyType _bodyType;

        public AvatarDescription(byte[] data)
        {
            _bodyType = AvatarBodyType.Male;
        }

        public static AvatarDescription CreateRandom()
        {
            return new AvatarDescription(EmptyDescription);
        }

        public static AvatarDescription CreateRandom(AvatarBodyType bodyType)
        {
            var desc = new AvatarDescription(EmptyDescription);
            desc._bodyType = bodyType;
            return desc;
        }

        public AvatarBodyType BodyType
        {
            get
            {
                return _bodyType;
            }
        }

        public byte[] Description
        {
            get
            {
                return EmptyDescription;
            }
        }

        public float Height
        {
            get
            {
                return 1.0f;
            }
        }

        public bool IsValid
        {
            get
            {
                return true;
            }
        }

    }
}
