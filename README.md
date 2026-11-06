# Audio

Source package: `org.mass4.turian.audio`.

This optional package owns event routing, resident and streamed voices, spatial panning, a bus mixer and silent server operation. Install a device/codec backend such as `org.mass4.turian.audio.miniaudio` for audible client output. Core and renderers do not reference audio or a GUI library.

Create an **Audio → Audio Mixer** asset and select it in Audio Settings. Buses start with Master, Music, SFX, Voice and UI; add named child buses and assign each `AudioEvent.Bus` to a routing name. Names are case-sensitive. The first bus must be the parentless Master; every other bus must reach it without cycles. Sessions compile a private copy before starting playback, so edit assets before starting Play.

Each bus processes its children and direct voices through an ordered effect chain, sidechain reduction and a linear fader before sending to its parent. Built-in effects are low/high-pass filters, stereo delay, reverb, compressor, limiter and distortion. `Wet` blends each effect with its input. Muting silences a subtree without stopping its voices; solo includes selected subtrees and their ancestor routing. Sidechain detectors read pre-fader voice signals, including a muted detector bus. Ducking has threshold, reduction, attack and release controls.

Snapshots specify target bus gain, mute/solo and optional wet amounts for the complete effect chain. Omitted buses and wet amounts restore authored defaults. A transition is one atomic queued command and interpolates gains and wet amounts over its requested sample count. Starting another snapshot continues from the current interpolated state. Effect parameters and routing are compiled at session setup; snapshots adjust gain and wet amounts.

Applications resolve `IAudioMixerService` from session services for settings-screen faders, mute/solo, snapshots and stereo peak/RMS meters:

```csharp
var mixer = Services.GetRequiredService<IAudioMixerService>();
mixer.SetBusVolume("Music", 0.5f);
mixer.SetBusMute("SFX", false);
mixer.TransitionTo("Underwater", 0.7f);
var meter = mixer.GetMeter("Music");
```

Call controls on the session's producer thread. A `false` result means the bounded command queue is full; requested state changes only when enqueueing succeeds. Fader/mute/solo controls ramp for 20 ms. Meter values are post-effects, post-ducking and post-fader, before any legacy global output gain and final clipping; they publish once per device block. Stop Play before changing the authored graph. Server sessions expose no-work silent services and allocate no device, decoder or DSP state.

Custom effects register `IAudioEffectFactory` through DI and select `Kind = Custom` with the factory's `CustomId`. Compilation runs at setup and returns a static `AudioEffectKernel` plus an optional disposable owner. The kernel receives an interleaved stereo buffer and frame count, must allocate nothing and must not block. Its state remains valid until callbacks stop; optional reset clears history between sessions, then the owner releases resources on disposal. Built-in DSP state is capped at 64 MiB per mixer. Up to 64 buses, 16 effects per bus and 64 snapshots are supported.

The optional `org.mass4.turian.audio.studio` brick supplies **View → Audio Mixer** with live controls during Play. It is installed in Studio independently of the project's runtime packages. The frontend supports project and Studio scope so the editor can share its GUI-independent contracts; the native device backend stays project-owned. Example 16 demonstrates settings bindings, underwater/pause snapshots and Voice-bus ducking.
