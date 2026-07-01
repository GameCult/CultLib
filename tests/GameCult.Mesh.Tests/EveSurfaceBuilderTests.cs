using System.Linq;
using FluentAssertions;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class EveSurfaceBuilderTests
{
    [Test]
    public void Builder_ComposesMainMenuLikeLegacyCultUiPanel()
    {
        var surface = EveSurface.Create("aetheria.main_menu.root")
            .Provider("aetheria", "game.menu")
            .TitleSubtitle("AETHERIA", "TERMINUS")
            .ButtonColumn("aetheria.main_menu.root.actions", actions => actions
                .Button("Continue", "aetheria.main_menu.root.continue")
                .Button("New Game", "aetheria.main_menu.root.new_game")
                .Button("Settings", "aetheria.main_menu.root.show_settings")
                .Button("Quit", "aetheria.main_menu.root.quit"))
            .Style("font.title.family", "Montserrat")
            .Style("font.body.family", "Ubuntu")
            .UpdatedAtUtc("2026-07-01T00:00:00.0000000Z")
            .Build();

        surface.ProviderId.Should().Be("aetheria");
        surface.ProviderKind.Should().Be("game.menu");
        surface.Title.Should().Be("AETHERIA TERMINUS");
        surface.UpdatedAtUtc.Should().Be("2026-07-01T00:00:00.0000000Z");
        surface.Surface.Id.Should().Be("aetheria.main_menu.root");
        surface.Surface.Root.Kind.Should().Be("surface");
        surface.Surface.Styles.Select(style => style.Name).Should().Contain(["font.title.family", "font.body.family"]);

        var rootChildren = surface.Surface.Root.Children;
        rootChildren[0].Kind.Should().Be("text.title");
        rootChildren[0].Props["value"].Should().Be("AETHERIA");
        rootChildren[1].Kind.Should().Be("text.subtitle");
        rootChildren[1].Props["value"].Should().Be("TERMINUS");

        var actions = rootChildren[2];
        actions.Kind.Should().Be("column");
        actions.Children.Should().HaveCount(4);
        actions.Children[2].Kind.Should().Be("control.button");
        actions.Children[2].Props["label"].Should().Be("Settings");
        actions.Children[2].Props["command"].Should().Be("aetheria.main_menu.root.show_settings");

        surface.Commands.Select(command => command.Command).Should().Equal(
            "aetheria.main_menu.root.continue",
            "aetheria.main_menu.root.new_game",
            "aetheria.main_menu.root.show_settings",
            "aetheria.main_menu.root.quit");
    }

    [Test]
    public void Form_ControlsCarryOperationAndStateBindings()
    {
        var nameOperation = CultMesh.OperationBinding("settings.player.name.set", "Set Player Name", "gamecult.settings.name.v1");
        var asteroidOperation = CultMesh.OperationBinding("settings.graphics.asteroids.toggle", "Toggle Asteroids");
        var nameBinding = new CultMeshStateBindingDescriptor(
            "value",
            "player.settings.name",
            "global:aetheria.player_settings.v1",
            "gamecult.aetheria.player_settings.v1");
        var asteroidBinding = new CultMeshStateBindingDescriptor(
            "value",
            "player.settings.show_asteroids",
            "global:aetheria.player_settings.v1",
            "gamecult.aetheria.player_settings.v1");

        var surface = EveSurface.Create("aetheria.main_menu.player_settings")
            .Form("aetheria.main_menu.player_settings.form", form => form
                .Text("Name", "Meta", nameOperation, nameBinding)
                .Toggle("Show Asteroids In Minimap", true, asteroidOperation, asteroidBinding)
                .Metric("Significant Digits", "3"))
            .Build();

        var form = surface.Surface.Root.Children.Single();
        form.Kind.Should().Be("form");
        form.Children.Should().HaveCount(3);

        var name = form.Children[0];
        name.Kind.Should().Be("control.text");
        name.Props["label"].Should().Be("Name");
        name.Props["value"].Should().Be("Meta");
        name.Props["command"].Should().Be("settings.player.name.set");
        name.StateBindings.Single().PointerId.Should().Be("player.settings.name");

        var toggle = form.Children[1];
        toggle.Kind.Should().Be("control.toggle");
        toggle.Props["value"].Should().Be("true");
        toggle.StateBindings.Single().PointerId.Should().Be("player.settings.show_asteroids");

        surface.Commands.Select(command => command.Command).Should().Equal(
            "settings.player.name.set",
            "settings.graphics.asteroids.toggle");
    }

    [Test]
    public void EmbeddedDocumentSlot_CarriesNestedSurfaceContract()
    {
        var surface = EveSurface.Create("station.inventory")
            .EmbeddedDocument(
                "cargo",
                "daemon:aetheria.inventory.station.v1",
                "gamecult.eve.surface.v1",
                "inventory.grid",
                new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "daemon-published CultUI surface"))
            .Build();

        var slot = surface.Surface.Root.Children.Single();
        slot.Kind.Should().Be("surface.slot");
        slot.EmbeddedDocuments.Should().ContainSingle();
        slot.EmbeddedDocuments[0].SlotId.Should().Be("cargo");
        slot.EmbeddedDocuments[0].DocumentId.Should().Be("daemon:aetheria.inventory.station.v1");
        slot.EmbeddedDocuments[0].SchemaId.Should().Be("gamecult.eve.surface.v1");
        slot.EmbeddedDocuments[0].RouteHint.Kind.Should().Be(nameof(CultMeshLocalityKind.SharedMemory));
    }

    [Test]
    public void SurfaceDocument_RoundTripsThroughCultCacheMessagePack()
    {
        var surface = EveSurface.Create("aetheria.main_menu.player_settings")
            .Provider("aetheria", "game.menu")
            .TitleSubtitle("Gameplay", "Settings")
            .Form("aetheria.main_menu.player_settings.form", form => form
                .Text(
                    "Name",
                    "Meta",
                    CultMesh.OperationBinding("settings.player.name.set", "Set Player Name"),
                    new CultMeshStateBindingDescriptor(
                        "value",
                        "player.settings.name",
                        "global:aetheria.player_settings.v1",
                        "gamecult.aetheria.player_settings.v1")))
            .Build();

        var payload = CultDocumentMessagePackSerialization.Serialize(surface);
        var roundTrip = CultDocumentMessagePackSerialization.Deserialize<EveSurfaceDocument>(payload);

        roundTrip.Surface.Id.Should().Be(surface.Surface.Id);
        roundTrip.Surface.Root.Children[2].Children.Single().StateBindings.Single().PointerId
            .Should().Be("player.settings.name");
        roundTrip.Commands.Single().Command.Should().Be("settings.player.name.set");
    }
}
