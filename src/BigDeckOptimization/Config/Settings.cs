using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BigDeckOptimization.Patches;

namespace BigDeckOptimization.Config;

/// <summary>
/// User choices for the optimizations: a master switch plus one switch per optimization, all on by default. Each
/// patch's own Enabled flag is the effective state: master AND its option AND not faulted. A patch that turned itself
/// off after an error (<see cref="PatchSafety.Fault"/>) stays off for the session whatever the settings say.
/// With BaseLib (optional) they are shown in its mod configuration menu and saved by it (see SettingsMenu); bdopt
/// changes them too, for the session only without BaseLib.
/// </summary>
public static class Settings
{
    public sealed class Option(string key, string label, string labelFr, string description, string descriptionFr,
        string[] faults, Action<bool> setEnabled, Func<string?>? unavailable = null)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string LabelFr { get; } = labelFr;
        public string Description { get; } = description;
        public string DescriptionFr { get; } = descriptionFr;
        /// <summary>Fault names (<see cref="PatchSafety.Fault"/>) that keep this optimization off.</summary>
        public string[] Faults { get; } = faults;
        public Action<bool> SetEnabled { get; } = setEnabled;
        /// <summary>Why the patch isn't applied here (optional mod absent, code changed), or null when it is.</summary>
        public Func<string?> Unavailable { get; } = unavailable ?? (() => null);
        public bool Wanted { get; internal set; } = true;
        public string? Fault => Faults.FirstOrDefault(PatchSafety.IsFaulted);
        public bool Effective => Master && Wanted && Fault == null;
    }

    public const string MasterKey = "enabled";

    public static bool Master { get; private set; } = true;

    public static IReadOnlyList<Option> Options { get; } = new Option[]
    {
        new("coop_checksum", "Co-op state check", "Vérification d'état en co-op",
            "Faster state check after every action in co-op (the biggest co-op lag with huge decks).",
            "Vérification d'état plus rapide après chaque action en co-op (le plus gros lag du co-op avec un énorme deck).",
            new[] { ListenerCache.Name, ListenerCache.SnapshotFault }, on => ListenerCache.SnapshotEnabled = on),
        new("combat_refresh", "Combat refresh", "Rafraîchissement en combat",
            "Faster card and UI refresh after every action in combat.",
            "Rafraîchissement plus rapide des cartes et de l'interface après chaque action en combat.",
            new[] { ListenerCache.Name }, on => ListenerCache.RefreshEnabled = on),
        new("hook_filter", "Skip empty hooks", "Ignorer les hooks vides",
            "Inside the two optimizations above, only asks the cards and relics that actually react to each check.",
            "Dans les deux optimisations ci-dessus, n'interroge que les cartes et reliques qui réagissent vraiment à chaque test.",
            new[] { HookFilter.Name }, on => HookFilter.Enabled = on),
        new("relic_input", "Relic bar input", "Entrées de la barre de reliques",
            "Stops hundreds of idle relic buttons from processing every mouse move.",
            "Évite que des centaines de boutons de reliques inactifs traitent chaque mouvement de souris.",
            new[] { IdleButtonInput.Name }, on => IdleButtonInput.Enabled = on),
        new("grid_sort", "Card grid sorting", "Tri des grilles de cartes",
            "Faster sorting of the deck view and other card grids.",
            "Tri plus rapide de la vue du deck et des autres grilles de cartes.",
            new[] { GridSortCache.Name }, on => GridSortCache.Enabled = on),
        new("grid_pool", "Card grid recycling", "Recyclage des grilles de cartes",
            "Faster recycling of card nodes when a card grid opens or is sorted.",
            "Recyclage plus rapide des cartes affichées à l'ouverture ou au tri d'une grille.",
            new[] { PoolSignalScan.Name }, on => PoolSignalScan.Enabled = on,
            () => PoolSignalScan.Applied ? null : "not applied (game code changed)"),
        new("pile_lookup", "Card pile lookup", "Recherche de la pile d'une carte",
            "Finds which pile a card is in without scanning every card (used by costs, keywords, playability...).",
            "Trouve la pile d'une carte sans parcourir toutes les cartes (utilisé par les coûts, mots-clés, jouabilité...).",
            new[] { PileIndex.Name }, on => PileIndex.Enabled = on,
            () => PileIndex.Applied ? null : "not applied (game code changed)"),
        new("card_ids", "Combat card IDs", "IDs des cartes en combat",
            "Skips re-scanning whole card piles for new cards on every card move (reshuffles, end of combat).",
            "Évite de reparcourir des piles entières à chaque carte déplacée (remélange, fin de combat).",
            new[] { CardDbScan.Name }, on => CardDbScan.Enabled = on,
            () => CardDbScan.Applied ? null : "not applied (game code changed)"),
        new("pile_counters", "Pile counters at combat end", "Compteurs des piles en fin de combat",
            "Updates the draw/discard/exhaust counters once when piles are emptied, instead of once per card.",
            "Met à jour les compteurs pioche/défausse/épuisement une seule fois quand les piles sont vidées, au lieu d'une fois par carte.",
            new[] { PileCounterBatch.Name }, on => PileCounterBatch.Enabled = on,
            () => PileCounterBatch.Applied ? null : "not applied (game code changed)"),
        new("fast_combat_start", "Fast combat start (50+ relics)", "Début de combat rapide (50+ reliques)",
            "Visual: with 50+ relics, plays the combat-start relic effects without their animation waits (like the game's Instant speed). Same results. Solo only: always on in co-op, so every player starts the combat at the same pace.",
            "Visuel : avec 50+ reliques, joue les effets de reliques du début de combat sans leurs temps d'animation (comme la vitesse Instantanée du jeu). Mêmes résultats. Solo uniquement : toujours actif en co-op, pour que tous les joueurs démarrent le combat au même rythme.",
            new[] { FastCombatStart.Name }, on => FastCombatStart.Enabled = on),
        new("limitless_tabs", "Limitless enchantment tabs", "Onglets d'enchantement Limitless",
            "With Limitless: reuses enchantment tabs instead of rebuilding them on every card render.",
            "Avec Limitless : réutilise les onglets d'enchantement au lieu de les reconstruire à chaque affichage de carte.",
            new[] { EnchantmentTabReuse.Name }, on => EnchantmentTabReuse.Enabled = on,
            () => EnchantmentTabReuse.Applied ? null : "not applied (Limitless absent or changed)"),
        new("listeners", "Effect listener lists", "Listes d'écoute des effets",
            "Builds the list of cards, relics and powers that each triggered effect asks with less copying and memory churn (same list, same order).",
            "Construit la liste des cartes, reliques et effets interrogés à chaque effet déclenché avec moins de copies et de mémoire (même liste, même ordre).",
            new[] { FastListeners.Name }, on => FastListeners.Enabled = on,
            () => FastListeners.Applied ? null : "not applied (game code changed)"),
        new("hook_skip", "Skip non-reacting cards", "Ignorer les cartes non concernées",
            "Each time the game processes an effect (card played, damage, turn end...), only asks the cards and enchantments whose type reacts to it (same results).",
            "À chaque effet traité par le jeu (carte jouée, dégâts, fin de tour...), n'interroge que les cartes et enchantements qui y réagissent (mêmes résultats).",
            new[] { HookSkip.Name }, on => HookSkip.Enabled = on,
            () => HookSkip.Applied ? null : "not applied (game code changed)"),
        new("hover_tips", "Creature hover tips", "Infobulles des créatures",
            "While the mouse is on a creature, rebuilds its tooltips after an action only when their content changed (they stay live).",
            "Quand la souris est sur une créature, reconstruit ses infobulles après une action seulement si leur contenu a changé (elles restent à jour).",
            new[] { HoverTipReuse.Name }, on => HoverTipReuse.Enabled = on,
            () => HoverTipReuse.Applied ? null : "not applied (game code changed)"),
        new("limitless_saves", "Limitless enchantment copies", "Copies d'enchantements Limitless",
            "With Limitless: keeps the saved copy of stacked enchantments when rebuilding it would give the same content, instead of rebuilding it after every action.",
            "Avec Limitless : garde la copie sauvegardée des enchantements empilés quand la reconstruire donnerait le même contenu, au lieu de la reconstruire après chaque action.",
            new[] { LimitlessReserialize.Name }, on => LimitlessReserialize.Enabled = on,
            () => LimitlessReserialize.Applied ? null : "not applied (Limitless absent or changed)"),
    };

    /// <summary>Raised after a choice changed through bdopt (not from the menu or when loading), so the menu saves it.</summary>
    public static event Action<string, bool>? Changed;

    public static Option? Find(string key) => Options.FirstOrDefault(o => o.Key == key);

    public static void SetMaster(bool on, bool notify = true)
    {
        bool changed = Master != on;
        Master = on;
        ApplyAll();
        if (changed && notify)
        {
            Changed?.Invoke(MasterKey, on);
        }
    }

    public static void SetOption(string key, bool on, bool notify = true)
    {
        Option? option = Find(key);
        if (option == null)
        {
            return;
        }
        bool changed = option.Wanted != on;
        option.Wanted = on;
        ApplyAll();
        if (changed && notify)
        {
            Changed?.Invoke(key, on);
        }
    }

    /// <summary>Pushes the effective state to every patch flag. Never turns a faulted patch back on.</summary>
    public static void ApplyAll()
    {
        foreach (Option option in Options)
        {
            option.SetEnabled(option.Effective);
        }
    }

    /// <summary>One line per optimization, for bdopt.</summary>
    public static string Status()
    {
        StringBuilder sb = new($"BigDeckOptimization: {(Master ? "on" : "OFF (vanilla)")}\n");
        foreach (Option option in Options)
        {
            string state = option.Unavailable() is { } why ? why
                : option.Fault is { } fault ? $"OFF, disabled after an error ({fault}), see log"
                : !option.Wanted ? (option.Key == "fast_combat_start" && Master ? "OFF in solo (setting), always on in co-op" : "OFF (setting)")
                : Master ? "on" : "off";
            sb.Append($"  {option.Label} [{option.Key}]: {state}\n");
        }
        return sb.ToString().TrimEnd();
    }
}
