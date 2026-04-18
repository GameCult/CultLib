# CultUI

`CultUI` is a Unity runtime UI composition and inspector framework, packaged for UPM and developed alongside a demo project that showcases its components, layout system, and reflective inspector workflow.

This repository serves two purposes:

- the source of the `org.gamecult.ui` package
- a Unity project for developing and demonstrating the package in a real scene

## Scope

CultUI is aimed at runtime, data-driven interface generation rather than hand-authoring every screen from scratch.

It is built for cases like:

- in-game settings menus
- debug and developer panels
- runtime inspectors for game objects and data models
- tools for exposing configurable values with minimal UI boilerplate
- reusable menu flows composed from prefabs and field handlers

## Features

- reflective runtime inspector generation for fields and properties
- prefab-driven resolver system for mapping value types to UI controls
- built-in field components for common input and display patterns
- foldouts and nested inspectors for structured data
- modal and color-picker style components
- reusable layout components for composing inspector rows and panels
- runtime menu composition for demos and application flows
- UPM-style package metadata and assembly definition for integration into Unity projects

## Package Layout

The package source lives under `Assets/UI`.

Key parts of the package include:

- `package.json`: package metadata for `org.gamecult.ui`
- `GameCult.Unity.UI.asmdef`: assembly definition for the runtime package
- `Generator.cs`: reflective inspector generation entry point
- `ReflectiveResolver.cs`: runtime resolver for prefab-backed field handlers and components
- `Components/`: built-in controls such as toggles, sliders, enum selectors, text input, buttons, labels, foldouts, and modal UI
- `Prefabs/`, `Resources/`, `Shaders/`: packaged assets used by the framework

## Demo Project

This repository also contains a Unity project used to develop and preview the package.

The demo project provides:

- a sample scene under `Assets/Scenes`
- package assets wired into a working Unity editor project
- demo menu flow showing how CultUI panels can be assembled at runtime
- a place to iterate on prefabs, resources, and inspector behavior without needing a separate host project

## Installation

### Local Package Development

If you are working in this repository directly:

1. Open the project in Unity `6000.4.2f1`.
2. Let Unity import packages and regenerate project files.
3. Open the demo scene in `Assets/Scenes`.

### Installing As A UPM Package

To consume CultUI from another Unity project, reference the package by local path or Git URL in your project's `manifest.json`.

Example local reference:

```json
{
  "dependencies": {
    "org.gamecult.ui": "file:../path-to-CultUI/Assets/UI"
  }
}
```

## Usage

At a high level, CultUI works by combining:

- a `ReflectiveResolver` asset that knows which prefabs handle which value types
- reusable UI component prefabs
- a `Generator` or `GeneratorPanel` that inspects an object and emits UI rows at runtime

Typical flow:

1. Create or configure a `ReflectiveResolver`.
2. Assign field and component prefabs to the resolver.
3. Place a `Generator` or `GeneratorPanel` in your canvas hierarchy.
4. Call `Inspect(...)` on a target object or use the helper methods for explicit UI composition.
5. Refresh displayed values as your runtime state changes.

CultUI can be used both reflectively and imperatively:

- reflective mode generates inspector UI from object members
- imperative mode builds menus and panels directly through helper methods and component composition

## Included Controls

The package currently includes components for patterns such as:

- labels
- text buttons
- boolean toggles
- text input
- numeric sliders
- increment/decrement fields
- enum selection
- foldouts
- modal overlays
- color selection workflows

## Requirements

- Unity `6000.4.2f1` for this repository's demo project
- `uGUI`
- TextMeshPro

The development project also includes additional Unity packages and NuGet-managed dependencies used by the demo environment.

## Repository Structure

- `Assets/UI`: package source
- `Assets/Scenes`: demo scene content
- `Assets/Prefabs`, `Assets/Resources`, `Assets/Scripts`: demo-project support assets and scripts
- `Packages`, `ProjectSettings`: Unity project configuration for package development and demonstration

## Development Notes

- the Unity project is the development host for the package
- generated `.csproj` and solution files are editor artifacts
- package-facing code should remain centered in `Assets/UI`
- demo-only code should stay separate from reusable package code
