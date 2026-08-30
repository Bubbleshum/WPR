
using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.GamerServices
{
    public class AchievementCollection : IList<Achievement>, ICollection<Achievement>, IEnumerable<Achievement>, IEnumerable, IDisposable
    {
        private List<Achievement> innerlist;

        public AchievementCollection()
        {
            innerlist = new List<Achievement>();
        }

        ~AchievementCollection()
        {
            Dispose(false);
        }

        #region Properties
        public int Count
        {
            get { return innerlist.Count; }
        }

        public Achievement this[int index]
        {
            get { return innerlist[index]; }
            set { throw new InvalidOperationException("Manually set achievement data is not allowed!"); }
        }

        public Achievement? this[string key]
        {
            get
            {
                Achievement? hit = innerlist.Find(achievement => achievement.Key == key);
                if (hit != null) return hit;

                // On a real WP7 device GetAchievements() returns the title's FULL achievement
                // catalogue (every achievement the game defines, earned or not), so a lookup by
                // key always resolves. In WPR the collection can be empty or partial when the
                // game ships no hardcoded catalogue under Database/Achievements/<id>/
                // (so the install-time seeder had nothing to seed).
                // Games iterate their own known achievement IDs and dereference the result
                // unconditionally — Kinectimals' AchievementManager.Initialise() does
                // `achievements[name].IsEarned`, which NREs on a miss and wedges the splash
                // screen forever (the loop keeps redrawing the splash but never transitions).
                // Hand back an unearned placeholder so the game can advance; AwardAchievement
                // still no-ops harmlessly when there's no backing DB row to flip.
                return new Achievement { Key = key, Name = key, IsEarned = false };
            }
            set { throw new InvalidOperationException("Manually set achievement data is not allowed!"); }
        }

        private bool isReadOnly = false;
        public bool IsReadOnly
        {
            get
            {
                return isReadOnly;
            }
        }

        #endregion Properties

        #region Public Methods
        public void Add(Achievement item)
        {
            if (item == null)
                throw new ArgumentNullException();

            if (innerlist.Count == 0)
            {
                innerlist.Add(item);
                return;
            }

            for (int i = 0; i < innerlist.Count; i++)
            {
                /*if (item.Position < innerlist[i].Position)
                {
                    this.innerlist.Insert(i, item);
                    return;
                }*/
            }

            this.innerlist.Add(item);
        }

        public void Clear()
        {
            innerlist.Clear();
        }

        public bool Contains(Achievement item)
        {
            return innerlist.Contains(item);
        }

        public void CopyTo(Achievement[] array, int arrayIndex)
        {
            innerlist.CopyTo(array, arrayIndex);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {

        }

        public int IndexOf(Achievement item)
        {
            return innerlist.IndexOf(item);
        }

        public void Insert(int index, Achievement item)
        {
            innerlist.Insert(index, item);
        }

        public bool Remove(Achievement item)
        {
            return innerlist.Remove(item);
        }

        public void RemoveAt(int index)
        {
            innerlist.RemoveAt(index);
        }

        public IEnumerator<Achievement> GetEnumerator()
        {
            return innerlist.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return innerlist.GetEnumerator();
        }

        #endregion Methods
    }
}