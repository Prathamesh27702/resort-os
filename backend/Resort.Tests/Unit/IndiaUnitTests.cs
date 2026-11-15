using Resort.Application.Compliance;
using Resort.Domain.Compliance;
using Resort.Infrastructure.Compliance;

namespace Resort.Tests.Unit;

/// <summary>Sprint 30 — the rules behind the UPI QR, the FSSAI number and the Form C sheet, without a database.</summary>
public class IndiaUnitTests
{
    [Theory]
    [InlineData("palmgrove@okhdfcbank", true)]
    [InlineData("9820012345@ybl", true)]
    [InlineData("resort.desk-1@upi", true)]
    [InlineData("palmgrove", false)]
    [InlineData("@okhdfcbank", false)]
    [InlineData("a@b", false)]
    [InlineData("palm grove@okhdfcbank", false)]
    [InlineData("palmgrove@9bank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_upi_id_is_a_name_at_a_bank_handle(string? id, bool valid) => Assert.Equal(valid, Upi.IsValidId(id));

    [Theory]
    [InlineData("10020099000456", true)]
    [InlineData("1002009900045", false)]
    [InlineData("100200990004567", false)]
    [InlineData("1002009900045A", false)]
    [InlineData("१००२००९९०००४५६", false)]
    [InlineData(null, false)]
    public void An_fssai_number_has_exactly_14_digits(string? number, bool valid) => Assert.Equal(valid, Upi.IsValidFssai(number));

    [Fact]
    public void The_upi_link_carries_the_id_the_name_and_the_exact_amount()
    {
        Assert.Equal("upi://pay?pa=palmgrove%40okhdfcbank&pn=Palm%20Grove%20Beach%20Resort&am=1234.50&cu=INR&tn=O00012",
            Upi.PayLink("palmgrove@okhdfcbank", "Palm Grove Beach Resort", 1234.5m, "O00012"));
        // Paise are rounded to two places, the way a bill shows them.
        Assert.Contains("am=99.99&", Upi.PayLink("a1@upi", "A", 99.994m, null));
        Assert.Contains("am=100.00&", Upi.PayLink("a1@upi", "A", 99.995m, null));
        // No amount (or zero): the guest types it. No note: no tn.
        Assert.Equal("upi://pay?pa=a1%40upi&pn=A&cu=INR", Upi.PayLink("a1@upi", "A", null, " "));
        Assert.DoesNotContain("am=", Upi.PayLink("a1@upi", "A", 0m, null));
        // Characters UPI apps choke on are dropped from the name and the note, and both are cut to 50.
        var link = Upi.PayLink("a1@upi", "Café & Bar <Goa>", 10m, new string('x', 80));
        Assert.Contains("pn=Caf%20%20Bar%20Goa", link);
        Assert.EndsWith("tn=" + new string('x', 50), link);
    }

    [Fact]
    public void The_qr_is_a_png()
    {
        var png = new QrCodes().Png(Upi.PayLink("palmgrove@okhdfcbank", "Palm Grove", 500m, "O00001"));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.InRange(png.Length, 200, 20_000);
    }

    [Fact]
    public void The_form_has_the_portals_fields_and_names_the_required_ones_still_empty()
    {
        Assert.Equal(26, FormCFields.All.Length);
        Assert.Equal(FormCFields.All.Length, FormCFields.Keys.Count);
        Assert.Equal([FormCFields.Personal, FormCFields.Passport, FormCFields.Visa, FormCFields.Arrival, FormCFields.Other], FormCFields.All.Select(f => f.Section).Distinct());
        Assert.Equal(24, FormCFields.HoursToFile);

        var all = FormCFields.All.Where(f => f.Required).ToDictionary(f => f.Key, _ => "x");
        Assert.Empty(FormCFields.Missing(all));
        all["visaNumber"] = " ";
        all.Remove("passportNumber");
        Assert.Equal(["Passport: Passport number", "Visa: Visa number"], FormCFields.Missing(all));
        // Surname and the phone numbers are optional on the portal.
        Assert.DoesNotContain(FormCFields.All, f => f.Required && f.Key is "surname" or "phoneInIndia" or "phoneAbroad");
    }

    [Theory]
    [InlineData("dateOfBirth", "1990-04-12", null)]
    [InlineData("dateOfBirth", "12/04/1990", "Date of birth must be a date, e.g. 14 Nov 2026.")]
    [InlineData("dateOfBirth", "1890-04-12", "Date of birth: 1890 can't be right. Check the year.")]
    [InlineData("timeOfArrivalHere", "14:30", null)]
    [InlineData("timeOfArrivalHere", "2:30 pm", "Time of arrival at this property must be a time on the 24-hour clock, e.g. 14:30.")]
    [InlineData("intendedDurationDays", "3", null)]
    [InlineData("intendedDurationDays", "0", "Intended stay here (days) must be a whole number of days from 1 to 3650.")]
    [InlineData("intendedDurationDays", "2.5", "Intended stay here (days) must be a whole number of days from 1 to 3650.")]
    [InlineData("sex", "f", null)]
    [InlineData("sex", "woman", "Sex: choose Male, Female or Other.")]
    [InlineData("employedInIndia", "NO", null)]
    [InlineData("employedInIndia", "maybe", "Employed in India: choose Yes or No.")]
    [InlineData("passportNumber", "C4FZ2X9P1C4FZ2X9P1C4FZ", "Passport number can have at most 20 characters; this has 22.")]
    public void A_typed_value_that_cant_be_right_says_why(string key, string value, string? problem) =>
        Assert.Equal(problem, FormCService.Problem(FormCFields.All.Single(f => f.Key == key), value));

    [Theory]
    [InlineData("sex", "f", "Female")]
    [InlineData("sex", "MALE", "Male")]
    [InlineData("sex", "x", "Other")]
    [InlineData("employedInIndia", "yes", "Yes")]
    [InlineData("passportNumber", "c4fz2x9p1", "C4FZ2X9P1")]
    [InlineData("visaType", "e-Tourist", "e-Tourist")]
    public void Typed_values_are_stored_one_way(string key, string value, string stored) =>
        Assert.Equal(stored, FormCService.Normal(FormCFields.All.Single(f => f.Key == key), value));

    [Fact]
    public void A_country_code_prints_as_its_name() => Assert.Equal(("Germany", "ZZ"), (FormCService.CountryName("DE"), FormCService.CountryName("ZZ")));
}
