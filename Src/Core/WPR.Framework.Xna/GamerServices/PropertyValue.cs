using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Xna.Framework.GamerServices
{
    internal abstract class PropertyValue
    {
        public bool IsChanged;
        public bool IsReadOnly = true;

        public abstract object GetValue();

        public abstract void SetValue(object value);
    }

    /// <summary>
    /// Concrete, type-agnostic <see cref="PropertyValue"/> used when a property is
    /// demand-created on first write (see <c>PropertyDictionary.GetProperty</c>). Stores
    /// the boxed value; <c>GetTypedValue</c> converts back out via <c>Convert.ChangeType</c>.
    /// </summary>
    internal sealed class ObjectPropertyValue : PropertyValue
    {
        private object _value;

        public override object GetValue() => this._value;

        public override void SetValue(object value)
        {
            if (object.Equals(value, this._value))
                return;
            this._value = value;
            this.IsChanged = true;
        }
    }
}
