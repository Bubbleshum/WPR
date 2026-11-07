using System.Collections;
using System.Collections.Generic;

namespace System.Windows.Input
{
    /// <summary>
    /// Shim for <c>System.Windows.Input.InputScope</c>: which software keyboard layout a
    /// <see cref="System.Windows.Controls.TextBox"/> asks for. Stored; WPR shows no on-screen keyboard.
    /// </summary>
    public class InputScope
    {
        public IList Names { get; } = new List<InputScopeName>();
    }

    /// <summary>Shim for <c>System.Windows.Input.InputScopeName</c>.</summary>
    public class InputScopeName
    {
        public InputScopeName()
        {
        }

        public InputScopeName(InputScopeNameValue nameValue) => NameValue = nameValue;

        public InputScopeNameValue NameValue { get; set; }
    }

    /// <summary>
    /// Shim for <c>System.Windows.Input.InputScopeNameValue</c>. The values are Windows' IS_*
    /// input scopes, which Silverlight used unchanged. Only 0-47, whose names are certain, are
    /// declared; a game passes these as numbers, so any other value is stored as given.
    /// </summary>
    public enum InputScopeNameValue
    {
        Default = 0,
        Url = 1,
        FullFilePath = 2,
        FileName = 3,
        EmailUserName = 4,
        EmailSmtpAddress = 5,
        LogOnName = 6,
        PersonalFullName = 7,
        PersonalNamePrefix = 8,
        PersonalGivenName = 9,
        PersonalMiddleName = 10,
        PersonalSurname = 11,
        PersonalNameSuffix = 12,
        PostalAddress = 13,
        PostalCode = 14,
        AddressStreet = 15,
        AddressStateOrProvince = 16,
        AddressCity = 17,
        AddressCountryName = 18,
        AddressCountryShortName = 19,
        CurrencyAmountAndSymbol = 20,
        CurrencyAmount = 21,
        Date = 22,
        DateMonth = 23,
        DateDay = 24,
        DateYear = 25,
        DateMonthName = 26,
        DateDayName = 27,
        Digits = 28,
        Number = 29,
        OneChar = 30,
        Password = 31,
        TelephoneNumber = 32,
        TelephoneCountryCode = 33,
        TelephoneAreaCode = 34,
        TelephoneLocalNumber = 35,
        Time = 36,
        TimeHour = 37,
        TimeMinorSec = 38,
        NumberFullWidth = 39,
        AlphanumericHalfWidth = 40,
        AlphanumericFullWidth = 41,
        CurrencyChinese = 42,
        Bopomofo = 43,
        Hiragana = 44,
        KatakanaHalfWidth = 45,
        KatakanaFullWidth = 46,
        Hanja = 47,
    }
}
