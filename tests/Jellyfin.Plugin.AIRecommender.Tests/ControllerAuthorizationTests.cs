using System.Reflection;
using Jellyfin.Plugin.AIRecommender.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class ControllerAuthorizationTests
{
    [Fact]
    public void Controller_requires_Jellyfin_elevation_policy()
    {
        var policies = typeof(AIRecommenderController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .ToList();

        Assert.Contains(Policies.RequiresElevation, policies);
    }

    [Fact]
    public void Controller_does_not_allow_plain_authenticated_access()
    {
        var attributes = typeof(AIRecommenderController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .ToList();

        Assert.DoesNotContain(attributes, attribute => string.IsNullOrWhiteSpace(attribute.Policy));
    }
}
