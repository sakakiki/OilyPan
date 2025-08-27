# CLAUDE.md

日本語で答えて

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OilyPan is a Unity 2022.3.62f1 project simulating liquid physics using GPU-accelerated shaders and CPU particle systems. The project features an oil simulation on a frying pan with realistic fluid dynamics, particle rendering, and device tilt input (gyroscope on mobile, keyboard/mouse on PC).

## Core Architecture

### Main Components

**ParticleDrawingController** (`Assets/ParticleDrawingController.cs`)
- Primary simulation controller managing 10,000+ particles
- Handles GPU-CPU data flow for velocity calculations
- Manages render textures for height maps, normal maps, and velocity fields
- Processes device input (gyroscope on mobile, WASD/mouse on PC) to tilt the pan
- Implements particle physics including friction, gravity, and bounds checking

**Shader Pipeline**
1. **HeightToNormal.shader** - Converts height maps to normal maps using central difference
2. **VelocityCalc.shader** - GPU compute shader calculating fluid velocity vectors from height/normal maps and gravity
3. **AdditiveParticle.shader** - Renders particles to height texture with additive blending

### Render Texture Flow

```
Particles → HeightMap RT (512x512) → NormalMap RT → VelocityMap RT (128x128) → CPU Readback → Particle Movement
```

- High-resolution rendering (512x512) for visual quality
- Low-resolution velocity calculation (128x128) for performance
- CPU readback at configurable intervals (default 0.05s) for particle physics

## Development Commands

### Unity Project
- **Play Mode**: Standard Unity play button or `Ctrl+P`
- **Build**: File → Build Settings → Build
- **Scene**: Main scene is `Assets/Scenes/SampleScene.unity`

### Key Configuration
- **Particle Count**: Adjustable up to 10,000+ (performance dependent)
- **Texture Resolutions**: Height/Normal at 512x512, Velocity at 128x128
- **Physics Parameters**: Friction, gravity scale, max velocity, velocity smoothing

## Important Implementation Notes

### GPU-CPU Communication
- The project uses `ReadPixels()` and `GetPixels()` for velocity texture readback
- Readback occurs at intervals to balance performance vs. responsiveness
- Velocity data flows from GPU shaders to CPU particle system

### Platform-Specific Input
- **Mobile**: Uses gyroscope for pan tilting when available
- **PC/Editor**: WASD keys and mouse for tilt control
- Input handling in `UpdatePanTiltInput()` method with automatic platform detection

### Shader Coordinate Mapping
- Critical coordinate mapping between C# and shaders:
  - `_Gravity.x = panLocal.x` (left-right tilt)  
  - `_Gravity.y = panLocal.z` (forward-back tilt)
- Incorrect mapping will cause tilt directions to be wrong or ignored

### Performance Considerations
- Particle rendering uses instanced drawing for performance
- Debug features (PNG saving, readback) can be disabled for production
- Velocity calculation resolution can be reduced for lower-end devices

## File Structure

```
Assets/
├── ParticleDrawingController.cs    # Main simulation controller
├── *.shader                        # GPU compute and rendering shaders
├── *.mat                          # Material assets
├── *.png                          # Texture assets (height maps, particle textures)
└── Scenes/SampleScene.unity       # Main scene
```

## Common Issues

### Particle Movement Problems
- Check velocity texture readback is working (enable debug output)
- Verify shader coordinate mapping matches C# gravity vector assignment
- Ensure rim height texture properly masks pan boundaries

### Performance Issues
- Reduce particle count or texture resolutions
- Disable debug features (PNG saving, verbose logging)
- Increase velocity sample interval

### Platform Input Issues
- Gyroscope requires device support and may need calibration
- PC input uses Unity's Input Manager (WASD mapped to Horizontal/Vertical axes)

## Shader Parameters

### VelocityCalc.shader Key Parameters
- `_kSlope`: Slope sensitivity (0.6 recommended for stability)
- `_kGravity`: Gravity contribution strength (1.0 default)  
- `_maxVel`: Velocity clamping limit (0.6 for safety)
- `_Damping`: Frame-to-frame damping (0.95 for stability)

These parameters are critical for simulation stability and should be adjusted carefully.