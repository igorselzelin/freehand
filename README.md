# Freehand

*[Português](#português) | [English](#english)*

---

## Português

Jogo de spray / grafite em realidade virtual e mista para **Meta Quest**, feito em Unity. Você pega uma lata de spray com as mãos, troca de cor e de bico (cap), pinta paredes e guarda suas latas personalizadas.

- **Versão atual:** 0.15.1
- **Plataforma:** Meta Quest (Android, OpenXR, min SDK 26), pacote `com.igor.freehand`
- **Engine:** Unity `6000.6.2f1`, Universal Render Pipeline

### Estado do jogo

O jogo está em desenvolvimento ativo (builds de v0.1 até v0.15.1). O que já existe:

**Spray e tinta**
- Lata de spray com gatilho analógico (bico e dedo animados), pintura em paredes via `PaintManager` / `PaintSurface`.
- Histórico de traços com **desfazer** (botão físico de undo).
- Tampas (caps) intercambiáveis, fisicamente encaixadas na lata (`CapSocket`, `CapRack`, `PhysicalCap`), cada uma com seu comportamento de spray.
- Sacudir a lata (`SprayCanShake`) com feedback visual, háptico e o som da bolinha chacoalhando.

**Cores**
- Paleta física na sala (`ColorPalette`) e **Color Orb**: uma esfera de cor invocada sobre a mão.

**Mãos e interação**
- Mãos rigged (`fps-hands.fbx`) com poses procedurais de dedos (aberta, segurando a lata, spray).
- Pegar/soltar objetos com física (`VRGrabber`, `VRGrabInteractable`).
- **Inspeção** do item na mão e **freestyle** (truques com a lata) pelo botão A/X.
- Feedback háptico nos controles.

**Oficina e "Minhas latas"**
- Bancada de trabalho com botões físicos (SALVAR etc.) e plataforma giratória.
- Latas salvas (`CanSkinStore`) exibidas em uma prateleira como réplicas em miniatura; gravação à prova de falhas (`SafeFile`).

**Realidade mista**
- Modo MR para pintar em **paredes reais** do ambiente (planos do AR Foundation, `MRSurfaceHub`, `MRWallStore`), com permissão de Android para ler a cena.
- Painel para entrar/sair do MR e saída de emergência: pressionar os dois thumbsticks.
- Código MR protegido pelo símbolo de compilação `FREEHAND_MR`.
- Sala de treino que se adapta ao espaço físico do jogador (`AdaptiveRoomController`).

**Áudio**
- Todo o áudio é **sintetizado em tempo de execução** (sem assets de áudio): spray, tampas, impactos, ambiente da sala (`SfxBank`, `Dsp`, `AudioManager`).

### Estrutura do projeto

```
Assets/
  Scenes/          SampleScene (cena principal)
  Scripts/
    Paint/         tinta, superfícies, histórico, undo
    Spray/         lata, caps, emissor, shake
    VR/            mãos, grab, calibração
    XR/            bootstrap de input
    MR/            realidade mista e paredes reais
    Tools/         paleta, Color Orb, racks, texto stencil
    Customization/ oficina, prateleira, skins de lata
    Interaction/   inspeção e freestyle
    Environment/   sala adaptativa
    Audio/         síntese e mixagem
    Editor/        construtores de cena, validadores, build
  Art/             modelos (lata, mãos), materiais, texturas
  Shaders/         ColorOrb, OrbHalo, MRPaintOverlay
tools/headless.ps1 execução headless do Unity
```

### Como rodar

1. Abra a pasta no **Unity 6000.6.2f1** (com o módulo Android instalado).
2. Abra `Assets/Scenes/SampleScene.unity`.
3. Para testar no Quest: *File > Build Profiles > Android*, conecte o headset em modo desenvolvedor e faça *Build And Run*. O script `Assets/Scripts/Editor/FreehandBuild.cs` também gera os APKs `freehand-quest-vX.Y.apk`.

### Pacotes principais

OpenXR 1.18, Input System 1.20, URP 17.6, glTFast 6.10.

### Observações

- APKs, `Library/`, logs e arquivos de IDE não são versionados (veja `.gitignore`).
- A cena e vários assets são gerados por scripts de editor (`V04SceneBuilder*`), então mudanças estruturais costumam passar por eles.

### Licença

Distribuído sob a licença [MIT](LICENSE).

---

## English

A spray-paint / graffiti game in virtual and mixed reality for **Meta Quest**, built with Unity. You pick up a spray can with your hands, switch colors and nozzle caps, paint walls and keep your customized cans.

- **Current version:** 0.15.1
- **Platform:** Meta Quest (Android, OpenXR, min SDK 26), package `com.igor.freehand`
- **Engine:** Unity `6000.6.2f1`, Universal Render Pipeline

### Game status

The game is under active development (builds from v0.1 to v0.15.1). What exists so far:

**Spray and paint**
- Spray can with an analog trigger (animated nozzle and finger), painting on walls through `PaintManager` / `PaintSurface`.
- Stroke history with **undo** (a physical undo button).
- Interchangeable caps physically seated on the can (`CapSocket`, `CapRack`, `PhysicalCap`), each with its own spray behavior.
- Can shaking (`SprayCanShake`) with visual and haptic feedback and the sound of the ball rattling inside.

**Colors**
- A physical color palette in the room (`ColorPalette`) and the **Color Orb**: a sphere of color summoned over your hand.

**Hands and interaction**
- Rigged hands (`fps-hands.fbx`) with procedural finger poses (open, holding the can, spraying).
- Physics-based grabbing and releasing of objects (`VRGrabber`, `VRGrabInteractable`).
- Item **inspection** and can **freestyle** (tricks with the can) on the A/X button.
- Haptic feedback on the controllers.

**Workshop and "My cans"**
- A workbench with physical buttons (SAVE, etc.) and a turntable.
- Saved cans (`CanSkinStore`) shown on a shelf as miniature replicas; crash-safe saving (`SafeFile`).

**Mixed reality**
- MR mode to paint on **real walls** of your environment (AR Foundation planes, `MRSurfaceHub`, `MRWallStore`), with the Android permission needed to read the scene.
- A panel to enter/leave MR, plus an emergency exit: press both thumbsticks.
- MR code is gated behind the `FREEHAND_MR` scripting define.
- A practice room that adapts to the player's physical space (`AdaptiveRoomController`).

**Audio**
- All audio is **synthesized at runtime** (no audio assets): spray, caps, impacts, room ambience (`SfxBank`, `Dsp`, `AudioManager`).

### Project structure

```
Assets/
  Scenes/          SampleScene (main scene)
  Scripts/
    Paint/         paint, surfaces, history, undo
    Spray/         can, caps, emitter, shake
    VR/            hands, grab, calibration
    XR/            input bootstrap
    MR/            mixed reality and real walls
    Tools/         palette, Color Orb, racks, stencil text
    Customization/ workshop, shelf, can skins
    Interaction/   inspection and freestyle
    Environment/   adaptive room
    Audio/         synthesis and mixing
    Editor/        scene builders, validators, build
  Art/             models (can, hands), materials, textures
  Shaders/         ColorOrb, OrbHalo, MRPaintOverlay
tools/headless.ps1 headless Unity run
```

### Getting started

1. Open the folder in **Unity 6000.6.2f1** (with the Android module installed).
2. Open `Assets/Scenes/SampleScene.unity`.
3. To test on Quest: *File > Build Profiles > Android*, connect the headset in developer mode and choose *Build And Run*. The `Assets/Scripts/Editor/FreehandBuild.cs` script also produces the `freehand-quest-vX.Y.apk` builds.

### Main packages

OpenXR 1.18, Input System 1.20, URP 17.6, glTFast 6.10.

### Notes

- APKs, `Library/`, logs and IDE files are not versioned (see `.gitignore`).
- The scene and many assets are generated by editor scripts (`V04SceneBuilder*`), so structural changes usually go through them.

### License

Distributed under the [MIT](LICENSE) license.
