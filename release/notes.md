Metadata Rules 1.0.2.0

- Allow later branches, including audio-language rules, to evaluate when the item or inherited series original language is confirmed absent. Direct Equals, Does not equal, Is one of, and Is none of comparisons treat that absence as a nonmatch.
- Keep unfinished, unsupported, and read-error original-language facts Unknown so lower-priority branches still defer. **Is missing** continues to match confirmed absence explicitly.
- Preserve missing-value behavior for all other fields. An absent original language does not establish the work's actual language.
- Show missing or unavailable actual values clearly in condition explanations instead of presenting them as inherited metadata preferences.
