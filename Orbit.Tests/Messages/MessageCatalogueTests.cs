using Orbit.Application.Messages;
using Orbit.Application.Requests;
using Orbit.Data.Entities;

namespace Orbit.Tests.Messages;

/// <summary>The triggers a message can be sent on (spec §6.7) are consistent with the enums and the token syntax.</summary>
public class MessageCatalogueTests
{
    /// <summary>MSG-001: every trigger is in the catalogue once, with Always first, distinct conditions and recipients in rank order.</summary>
    [Fact]
    public void Every_trigger_is_defined_once_with_always_first()
    {
        Assert.Equal(Enum.GetValues<MessageTrigger>(), MessageCatalogue.All.Select(t => t.Trigger));
        foreach (var t in MessageCatalogue.All)
        {
            Assert.Equal(MessageCondition.Always, t.Conditions[0]);
            Assert.Equal(t.Conditions.Count, t.Conditions.Distinct().Count());
            Assert.NotEmpty(t.Recipients);
            Assert.Equal(t.Recipients.OrderBy(k => k), t.Recipients);
            Assert.Equal(t.Recipients.Count, t.Recipients.Distinct().Count());
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Label);
            Assert.Contains(t.Group, new[] { MessageCatalogue.TasksGroup, MessageCatalogue.ProjectsGroup, MessageCatalogue.RequestsGroup });
            Assert.NotEqual(t.Trigger.ToString(), t.Label);
        }
        Assert.All(Enum.GetValues<MessageCondition>(), c => Assert.Contains(MessageCatalogue.All, t => t.Offers(c)));
        Assert.All(Enum.GetValues<MessageRecipientKind>(), k => Assert.Contains(MessageCatalogue.All, t => t.Offers(k)));
    }

    /// <summary>MSG-001: each trigger's tokens are unique and parse as tokens; every trigger names the recipient and actor, and links to what it is about - and only to that.</summary>
    [Fact]
    public void Tokens_are_unique_parseable_and_link_to_the_task_or_request()
    {
        foreach (var t in MessageCatalogue.All)
        {
            var names = t.Tokens.Select(x => x.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (var token in t.Tokens)
            {
                Assert.Equal([token.Name], RequestTokenRules.Parse(token.Text));
                Assert.False(string.IsNullOrWhiteSpace(token.Label), token.Name);
                Assert.False(string.IsNullOrWhiteSpace(token.Sample), token.Name);
            }
            Assert.Contains("recipient.name", names);
            Assert.Contains("actor.name", names);
            string[] links = ["task.url", "project.url", "request.url"];
            var own = t.IsAboutTasks ? "task.url" : t.IsAboutProjects ? "project.url" : "request.url";
            Assert.Equal([own], links.Where(names.Contains));
        }
    }

    /// <summary>MSG-001: request triggers never go to a department's members, who can't open a request (§6.20).</summary>
    [Fact]
    public void Request_triggers_go_only_to_people_on_the_request()
    {
        foreach (var t in MessageCatalogue.All.Where(t => t.IsAboutRequests))
            Assert.All(t.Recipients, k => Assert.Contains(k, new[] { MessageRecipientKind.StepPerson, MessageRecipientKind.Requester }));
        Assert.True(MessageCatalogue.Get(MessageTrigger.TaskCreated).Offers(MessageRecipientKind.DepartmentMembers));
        Assert.True(MessageCatalogue.Get(MessageTrigger.TaskAssigned).Offers(MessageRecipientKind.AddedAssignees));
        Assert.False(MessageCatalogue.Get(MessageTrigger.TaskCreated).Offers(MessageRecipientKind.AddedAssignees));
    }

    /// <summary>MSG-001 (projects): a project's messages go to its owner and its department's members, and only a project's go to its owner.</summary>
    [Fact]
    public void Project_triggers_go_to_the_owner_and_the_department()
    {
        var projects = MessageCatalogue.All.Where(t => t.IsAboutProjects).ToList();
        Assert.Equal([MessageTrigger.ProjectCreated, MessageTrigger.ProjectStatusChanged, MessageTrigger.ProjectOwnerChanged, MessageTrigger.ProjectCommented],
            projects.Select(t => t.Trigger));
        Assert.All(projects, t => Assert.Equal([MessageRecipientKind.ProjectOwner, MessageRecipientKind.DepartmentMembers], t.Recipients));
        Assert.All(MessageCatalogue.All.Where(t => !t.IsAboutProjects), t => Assert.False(t.Offers(MessageRecipientKind.ProjectOwner), t.Label));
        Assert.Equal([MessageCatalogue.TasksGroup, MessageCatalogue.ProjectsGroup, MessageCatalogue.RequestsGroup], MessageCatalogue.Groups.Select(g => g.Key));
    }
}
