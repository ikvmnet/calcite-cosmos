using System;
using System.Collections.Generic;
using System.Globalization;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The tokens a theory writes its stored forms and extensions as, and the objects they stand for.
    /// </summary>
    /// <remarks>
    /// Filled once, from a theory's rules, before anything is asked, and read without a lock after. A form or an
    /// extension no rule states has no token, and nothing can be known of it.
    /// </remarks>
    internal sealed class JsonFactVocabulary
    {

        readonly Dictionary<IJsonStoredForm, string> _forms = new();
        readonly List<KeyValuePair<IJsonStoredForm, string>> _formList = new();
        readonly Dictionary<JsonClaim.Extension, string> _extensions = new();
        readonly List<KeyValuePair<JsonClaim.Extension, string>> _extensionList = new();

        /// <summary>
        /// Gets every stored form with a token, and the token.
        /// </summary>
        public IReadOnlyList<KeyValuePair<IJsonStoredForm, string>> Forms => _formList;

        /// <summary>
        /// Gets every extension with a token, and the token.
        /// </summary>
        public IReadOnlyList<KeyValuePair<JsonClaim.Extension, string>> Extensions => _extensionList;

        /// <summary>
        /// Returns the token of a stored form, giving it one where it has none.
        /// </summary>
        public string Form(IJsonStoredForm form)
        {
            if (_forms.TryGetValue(form, out var token) == false)
            {
                token = "form:" + _forms.Count.ToString(CultureInfo.InvariantCulture);
                _forms[form] = token;
                _formList.Add(new(form, token));
            }

            return token;
        }

        /// <summary>
        /// Returns the token of a stored form, if it has one.
        /// </summary>
        public bool TryForm(IJsonStoredForm form, out string token) => _forms.TryGetValue(form, out token!);

        /// <summary>
        /// Returns the token of an extension, giving it one where it has none.
        /// </summary>
        public string Extension(JsonClaim.Extension extension)
        {
            if (_extensions.TryGetValue(extension, out var token) == false)
            {
                token = "extension:" + _extensions.Count.ToString(CultureInfo.InvariantCulture);
                _extensions[extension] = token;
                _extensionList.Add(new(extension, token));
            }

            return token;
        }

        /// <summary>
        /// Returns the token of an extension, if it has one.
        /// </summary>
        public bool TryExtension(JsonClaim.Extension extension, out string token) => _extensions.TryGetValue(extension, out token!);

    }

}
