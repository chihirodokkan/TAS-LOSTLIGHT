# Lost Light TAS (BepInEx 6 Mono, Windows) MAJ A VENIR


Plugin TAS pour Lost Light : enregistrement et relecture d'inputs déterministes (clavier + caméra), savestates, détection de désynchro, édition de frames, analyse CSV et serveur TCP pour l'appli WPF.

## Build

```bat
dotnet build "TASPlugin (1).csproj" -c Release
```

Copier `LostLightTAS.dll` dans `BepInEx\plugins\`. Surcharges possibles : `-p:GameDir="...\Lost Light"`.

## Fichiers (dans `BepInEx\config\LostLightTAS\`)

| Fichier | Rôle |
|---|---|
| `inputs.txt` | le « film » (une ligne par frame) |
| `backups\` | copies automatiques de `inputs.txt` avant écrasement (30 max) |
| `states\slotN.sav` | savestates sur disque (slots 1 à 9) |
| `analysis\run_*.csv` | analyse de run (F8/F9) |

La config BepInEx (`woofle.lostlight.tas.cfg`) permet de régler : graine, framerate, seuil de désynchro, pause auto, port, et **toutes les touches**.

## Touches par défaut

| Touche | Action |
|---|---|
| F10 / F11 | pause / une frame |
| F2 | turbo (framerate non limité) |
| F5 | lecture du film |
| F6 | enregistrement (écrase, avec sauvegarde de secours) |
| F7 | **rerecord** : continue l'enregistrement depuis la frame de lecture courante |
| F3 / F4 | sauvegarder / charger le slot courant |
| PgUp / PgDn | changer de slot |
| Inser / Suppr | dupliquer / supprimer la frame courante (édition) |
| Retour arrière | annuler la dernière édition |
| F8 / F9 | analyse CSV : démarrer / arrêter |
| F12 | HUD |

## Format du film

`x y [jump] [crouch] [interact] [sprint] [respawn] [cam=yaw/pitch[/axeH/axeV]] [pos=x/y/z]`

- `x` gauche/droite, `y` arrière/avant (−1..1). Les boutons listés sont maintenus pendant la frame.
- Le jeu lie Move à `<Keyboard>/w,a,s,d` (positions physiques) : sur AZERTY, ce sont Z, Q, S, D. Sprint = `[`, Respawn = `R`.
- `cam=` : le mouvement dépend de `mainCam.eulerAngles.y` ; la caméra est donc enregistrée et réimposée en lecture (les `CinemachineInputAxisController` sont désactivés pendant la lecture).
- `pos=` : position de référence ; en lecture, un écart supérieur au seuil déclenche l'alerte de désynchro (HUD rouge, événement `DESYNC`, pause automatique si activée).
- Les champs `cam=` et `pos=` sont optionnels : les anciens fichiers restent lisibles (mais non déterministes sans caméra).

## Déterminisme

- `Time.captureFramerate` fixe, `vSync` coupé.
- `UnityEngine.Random.InitState(graine)` au début de chaque lecture/enregistrement ; `Random.state` est sauvegardé dans les savestates.
- `InputSystem.Update()` est appelé après l'injection : touches et caméra de la ligne N s'appliquent dans la frame N, comme à l'enregistrement (`ImmediateInjection`).

## Savestates

Capturent : position/rotation, champs de valeur de `PlayerMovementCC` et `PlayerRespawn`, point de respawn, caméra (axes Cinemachine), état aléatoire, index de lecture et compteur de frames TAS. **Ne rembobinent pas** : objets du monde, coroutines, animations, autres composants. Un slot ne se charge que dans la scène où il a été créé.

Workflow typique : lire le film → pause → `F4` → `F7` (rerecord) → rejouer la section → `F6`/`F7` pour terminer.

## Serveur TCP (127.0.0.1:7777)

Plugin → appli : `STATE frame px py pz vx vy vz scène`, `EVENT ...` (PLAY, STOP, REC_START, REC_STOP, RERECORD, SAVE, LOAD, SCENE, RESPAWN, DESYNC), `INFO ...` (réponse à STATUS).

Appli → plugin : `PAUSE`, `STEP`, `SAVE [n]`, `LOAD [n]`, `SLOT n`, `PLAY`, `REC`, `RERECORD`, `TURBO`, `ANALYSE_START`, `ANALYSE_STOP`, `RELOAD`, `GOTO n`, `SET idx x y [flags]`, `INSERT idx [x y flags]`, `DELETE idx [n]`, `UNDO`, `STATUS`.

## Analyse CSV

`frame;tasframe;time;x;y;z;speed;dist;keys;axisH;axisV;yaw;grounded;jumps;scene;timescale;drift` — `keys` = Z S Q D Espace Ctrl E Sprint R (9 caractères 0/1).
