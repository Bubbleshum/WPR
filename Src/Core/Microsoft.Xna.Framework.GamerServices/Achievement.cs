using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System;

using WPR.Common;

namespace Microsoft.Xna.Framework.GamerServices
{
    public class Achievement
    {
        public Achievement()
        {
        }

        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Key, Column(Order = 0)]
        public int Id { get; set; }

        public string _IconPath { get; set; }

        public string Description { get; set; }

        public bool DisplayBeforeEarned { get; set; }

        public DateTime EarnedDateTime { get; set; }

        public bool EarnedOnline { get; set; }

        public int GamerScore { get; set; }

        public string HowToEarn { get; set; }

        public bool IsEarned { get; set; }

        public string Key { get; set; }

        public string Name { get; set; }

        public string OwnProductId { get; set; }

        public Stream GetPicture()
        {
            // Read-only + FileShare.ReadWrite: the unlock toast displays the same
            // icon PNG and holds a handle on it, so a concurrent GetPicture() that
            // asked for the default ReadWrite access threw "being used by another
            // process". Requesting only read and sharing fully avoids the collision.
            Stream res = new FileStream(
                Configuration.Current!.DataPath(_IconPath),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return res;
        }
    }
}