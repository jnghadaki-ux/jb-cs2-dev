SWBS prisoner skeleton fix
==========================

The 0.18 SWBS prisoners were built on CS2's mesh bones only and miss the helper bones CS2's animations and attachments
expect ("Attempted to attach attachment ... to an unknown bone", bad weapon holding). This rebuilds each prisoner on the
skeleton of a shipping CS2 agent (agents/models/ctm_diver/ctm_diver_varianta) and copies that agent's attachments,
hitboxes, ragdoll physics, bone constraints, animation skeletons and graphs. The prisoner mesh is moved bone by bone
(the frames were verified against the compiled .vmdl_c files to 1e-4).

1. Get ctm_diver_varianta.vmdl_c out of game/csgo/pak01_dir.vpk (Source 2 Viewer, agents/models/ctm_diver/, Export as is).
2. dotnet run --project decompile -- ctm_diver_varianta.vmdl_c diver_a        (writes the .vmdl + .dmx files)
3. python retarget.py <pack>/swbs_jailbreak diver_a out                         (needs numpy)
4. Copy out/characters over the addon's content folder and compile the pack as usual.
