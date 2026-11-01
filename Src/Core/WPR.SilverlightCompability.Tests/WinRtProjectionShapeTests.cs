using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Store;
using Windows.Foundation.Collections;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Game IL binds WinRT members by .NET's projection of them: a WinRT event as
/// <c>EventRegistrationToken add_X(handler)</c> / <c>void remove_X(token)</c>, a collection by the
/// projected interface. A member declared any other way is a MissingMethodException in the game.
/// </summary>
public class WinRtProjectionShapeTests
{
    [Fact]
    public void LicenseChanged_is_a_token_event()
    {
        var add = typeof(LicenseInformation).GetMethod("add_LicenseChanged", new[] { typeof(LicenseChangedEventHandler) });
        var remove = typeof(LicenseInformation).GetMethod("remove_LicenseChanged", new[] { typeof(EventRegistrationToken) });

        Assert.NotNull(add);
        Assert.Equal(typeof(EventRegistrationToken), add!.ReturnType);
        Assert.NotNull(remove);
        Assert.Equal(typeof(void), remove!.ReturnType);
    }

    [Fact]
    public void LicenseChanged_token_round_trips()
    {
        var license = CurrentApp.LicenseInformation;
        var token = license.add_LicenseChanged(() => { });
        license.remove_LicenseChanged(token);
    }

    [Fact]
    public void Store_collections_have_their_projected_types()
    {
        Assert.Equal(typeof(IReadOnlyDictionary<string, ProductListing>),
            typeof(ListingInformation).GetMethod("get_ProductListings")!.ReturnType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, ProductLicense>),
            typeof(LicenseInformation).GetMethod("get_ProductLicenses")!.ReturnType);
    }

    [Fact]
    public void CoreApplication_Properties_is_a_property_set()
    {
        Assert.Equal(typeof(IPropertySet), typeof(CoreApplication).GetMethod("get_Properties")!.ReturnType);

        CoreApplication.Properties["wpr-test"] = 1;
        Assert.Equal(1, CoreApplication.Properties["wpr-test"]);
        CoreApplication.Properties.Remove("wpr-test");
    }

    [Fact]
    public void ValueSet_is_a_property_set()
    {
        IPropertySet set = new ValueSet { ["a"] = "b" };
        Assert.Equal("b", set["a"]);
    }
}
