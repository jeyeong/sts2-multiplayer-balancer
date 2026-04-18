# Slay the Spire 2 - Multiplayer Balancer Mod

This Mod introduces modifications that seek to make multiplayer games more balanced, especially those with 3+ players.

Current modifications:

- **Team Death:** If one member of the team dies, the entire team loses (inspired by the STS board game).
- **Multiplayer Card Nerfs:** Nerfs team damage-increasing cards (Flanking, Knockdown) by making them only last for one attack.

## Setup

This project was set up following the instructions here: https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup.

## Build

To build this project, run the following command.

```
dotnet build
```

The generated files can be found in `$(Sts2Path)/mods/STS2MultiplayerBalancer`.

## Testing

To test out this Mod in game, the Dev Console, opened using `` ` ``, is helpful. There, you can use commands like the below.

```
// Multiplayer testing
multiplayer test

// In-game utilities
card FLANKING // adds FLANKING to hand
energy 99
draw 5

// Other utilities
die
```
