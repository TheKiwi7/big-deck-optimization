using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Saves.Runs;

// Rule B's test twins (Bench/ForeignShapesLimitless.cs installs them): copies of BaseLib 3.4.8's SavedSpireFieldPatch, as a mod
// bundling BaseLib's code has (Acheron). Their loops are written out (GetEnumerator, while, finally Dispose) so that this
// compiler gives BaseLib's IL: its foreach kept a local for the element that BaseLib's compiler doesn't (the twin check of
// `foreignpatch lim-twin` prints whether the IL matches, and the accept scenario expects it to). Each lives in a namespace of its own so that, its namespace aside (OwnNormalizedIl names
// this assembly's types without it), it reads as BaseLib's class: same class, interface and field names, same code. Only the
// accepted one has BaseLib's code exactly; each control differs in one point the rule must refuse.

namespace BigDeckOptimization.Bench.Twins
{
    /// <summary>BaseLib's ISavedSpireField's members as its patch uses them.</summary>
    public interface ISavedSpireField
    {
        string Name { get; }
        Type TargetType { get; }
        void Export(object model, SavedProperties props);
        void Import(object model, SavedProperties props);
    }

    /// <summary>A field registered by the "registered field" control: it targets no model type (its code never runs).</summary>
    internal sealed class InertField : ISavedSpireField
    {
        public string Name => "bdtest_twin_field";
        public Type TargetType => typeof(InertField);
        public void Export(object model, SavedProperties props)
        {
        }

        public void Import(object model, SavedProperties props)
        {
        }
    }
}

namespace BigDeckOptimization.Bench.Twins.Accepted
{
    using BigDeckOptimization.Bench.Twins;

    /// <summary>BaseLib's code exactly (accepted while its list is empty; <see cref="Register"/> makes it hold a field).</summary>
    internal static class SavedSpireFieldPatch
    {
        private static readonly List<ISavedSpireField> RegisteredFields = new List<ISavedSpireField>();

        internal static void Register(ISavedSpireField field) => RegisteredFields.Add(field);

        private static IEnumerable<ISavedSpireField> GetFieldsForModel(object model)
        {
            return RegisteredFields.Where((ISavedSpireField f) => f.TargetType.IsInstanceOfType(model));
        }

        private static void PostfixFromInternal(ref SavedProperties? __result, object model)
        {
            SavedProperties savedProperties = __result ?? new SavedProperties();
            bool flag = false;
            IEnumerator<ISavedSpireField> fields = GetFieldsForModel(model).GetEnumerator();
            try
            {
                while (fields.MoveNext())
                {
                    fields.Current.Export(model, savedProperties);
                    flag = true;
                }
            }
            finally
            {
                if (fields != null)
                {
                    fields.Dispose();
                }
            }
            if (__result == null && flag)
            {
                __result = savedProperties;
            }
        }

        private static void PostfixFillInternal(SavedProperties __instance, object model)
        {
            IEnumerator<ISavedSpireField> fields = GetFieldsForModel(model).GetEnumerator();
            try
            {
                while (fields.MoveNext())
                {
                    fields.Current.Import(model, __instance);
                }
            }
            finally
            {
                if (fields != null)
                {
                    fields.Dispose();
                }
            }
        }
    }
}

namespace BigDeckOptimization.Bench.Twins.Unfiltered
{
    using BigDeckOptimization.Bench.Twins;

    /// <summary>Control: GetFieldsForModel gives every registered field (no filter on the model).</summary>
    internal static class SavedSpireFieldPatch
    {
        private static readonly List<ISavedSpireField> RegisteredFields = new List<ISavedSpireField>();

        private static IEnumerable<ISavedSpireField> GetFieldsForModel(object model)
        {
            return RegisteredFields;
        }

        private static void PostfixFromInternal(ref SavedProperties? __result, object model)
        {
            SavedProperties savedProperties = __result ?? new SavedProperties();
            bool flag = false;
            IEnumerator<ISavedSpireField> fields = GetFieldsForModel(model).GetEnumerator();
            try
            {
                while (fields.MoveNext())
                {
                    fields.Current.Export(model, savedProperties);
                    flag = true;
                }
            }
            finally
            {
                if (fields != null)
                {
                    fields.Dispose();
                }
            }
            if (__result == null && flag)
            {
                __result = savedProperties;
            }
        }
    }
}

namespace BigDeckOptimization.Bench.Twins.AlwaysStores
{
    using BigDeckOptimization.Bench.Twins;

    /// <summary>Control: the result stored whenever it was null (a null result becomes an empty object).</summary>
    internal static class SavedSpireFieldPatch
    {
        private static readonly List<ISavedSpireField> RegisteredFields = new List<ISavedSpireField>();

        private static IEnumerable<ISavedSpireField> GetFieldsForModel(object model)
        {
            return RegisteredFields.Where((ISavedSpireField f) => f.TargetType.IsInstanceOfType(model));
        }

        private static void PostfixFromInternal(ref SavedProperties? __result, object model)
        {
            SavedProperties savedProperties = __result ?? new SavedProperties();
            bool flag = false;
            IEnumerator<ISavedSpireField> fields = GetFieldsForModel(model).GetEnumerator();
            try
            {
                while (fields.MoveNext())
                {
                    fields.Current.Export(model, savedProperties);
                    flag = true;
                }
            }
            finally
            {
                if (fields != null)
                {
                    fields.Dispose();
                }
            }
            if (__result == null || flag)
            {
                __result = savedProperties;
            }
        }
    }
}

namespace BigDeckOptimization.Bench.Twins.ImpureInitializer
{
    using BigDeckOptimization.Bench.Twins;

    /// <summary>Control: BaseLib's code, but its class's initializer does more than make the list (it counts its runs).</summary>
    internal static class SavedSpireFieldPatch
    {
        internal static int Initialized;

        private static readonly List<ISavedSpireField> RegisteredFields = Make();

        private static List<ISavedSpireField> Make()
        {
            Initialized++;
            return new List<ISavedSpireField>();
        }

        private static IEnumerable<ISavedSpireField> GetFieldsForModel(object model)
        {
            return RegisteredFields.Where((ISavedSpireField f) => f.TargetType.IsInstanceOfType(model));
        }

        private static void PostfixFromInternal(ref SavedProperties? __result, object model)
        {
            SavedProperties savedProperties = __result ?? new SavedProperties();
            bool flag = false;
            IEnumerator<ISavedSpireField> fields = GetFieldsForModel(model).GetEnumerator();
            try
            {
                while (fields.MoveNext())
                {
                    fields.Current.Export(model, savedProperties);
                    flag = true;
                }
            }
            finally
            {
                if (fields != null)
                {
                    fields.Dispose();
                }
            }
            if (__result == null && flag)
            {
                __result = savedProperties;
            }
        }
    }
}
