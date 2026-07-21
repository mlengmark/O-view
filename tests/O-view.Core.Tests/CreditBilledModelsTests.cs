using OView.Core.Models;

namespace OView.Core.Tests;

public class CreditBilledModelsTests
{
    [Theory]
    [InlineData("claude-fable-5", true)]    // verified against billing (issue #3, finding)
    [InlineData("claude-mythos-5", true)]   // same tier/pricing, by parity
    [InlineData("claude-opus-4-8", false)]  // the plan model — draws from the window
    [InlineData("claude-sonnet-5", false)]  // no evidence it bills to credits
    [InlineData("unknown", false)]
    public void IsCreditBilled_ClassifiesByEvidence(string model, bool expected)
    {
        Assert.Equal(expected, CreditBilledModels.IsCreditBilled(model));
    }
}
