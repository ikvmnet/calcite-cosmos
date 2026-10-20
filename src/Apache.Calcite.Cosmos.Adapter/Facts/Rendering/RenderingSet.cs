namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The targets a statement must hold on. A key is settled in a set once every target it serves has measured it, and
    /// its answers are their union; which set a statement is checked under is the host's choice.
    /// </summary>
    internal enum RenderingSet
    {

        /// <summary>Azure alone.</summary>
        Production,

        /// <summary>Azure and the emulator: a capability holds only where both accept it.</summary>
        Portable,

        /// <summary>The emulator alone, as CI runs it.</summary>
        Emulator,

        /// <summary>Calcite alone, whose connective rows license composition.</summary>
        Calcite,

    }

}
