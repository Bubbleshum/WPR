using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework.GamerServices
{
    public sealed class GameDefaults
    {
        internal GameDefaults()
        {
        }

        public bool AccelerateWithButtons
        {
            get
            {
                return false;
            }
        }

        public bool AutoAim
        {
            get
            {
                return true;
            }
        }

        public bool AutoCenter
        {
            get
            {
                return true;
            }
        }

        public bool BrakeWithButtons
        {
            get
            {
                return false;
            }
        }

        public ControllerSensitivity ControllerSensitivity
        {
            get
            {
                return ControllerSensitivity.Medium;
            }
        }

        public GameDifficulty GameDifficulty
        {
            get
            {
                return GameDifficulty.Normal;
            }
        }

        public bool InvertYAxis
        {
            get
            {
                return false;
            }
        }

        public bool ManualTransmission
        {
            get
            {
                return false;
            }
        }

        public bool MoveWithRightThumbStick
        {
            get
            {
                return false;
            }
        }

        public Color? PrimaryColor
        {
            get
            {
                return null;
            }
        }

        public RacingCameraAngle RacingCameraAngle
        {
            get
            {
                return RacingCameraAngle.Back;
            }
        }

        public Color? SecondaryColor
        {
            get
            {
                return null;
            }
        }

    }
}
