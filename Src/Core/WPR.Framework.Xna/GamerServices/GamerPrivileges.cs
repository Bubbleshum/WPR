using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Xna.Framework.GamerServices
{
    public sealed class GamerPrivileges
    {
        internal GamerPrivileges()
        {
        }

        public GamerPrivilegeSetting AllowCommunication
        {
            get
            {
                return GamerPrivilegeSetting.Everyone;
            }
        }

        public bool AllowOnlineSessions
        {
            get
            {
                return true;
            }
        }

        public GamerPrivilegeSetting AllowProfileViewing
        {
            get
            {
                return GamerPrivilegeSetting.Everyone;
            }
        }

        public bool AllowPurchaseContent
        {
            get
            {
                return false; //!
            }
        }

        public bool AllowTradeContent
        {
            get
            {
                return true;
            }
        }

        public GamerPrivilegeSetting AllowUserCreatedContent
        {
            get
            {
                return GamerPrivilegeSetting.Everyone;
            }
        }
    }
}
