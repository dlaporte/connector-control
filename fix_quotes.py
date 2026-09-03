#!/usr/bin/env python3
import sys

# MasterStore.cs
master_store = open("windows/src/ConnectorControl.Core/MasterStore.cs", "r", encoding="utf-8").read()

# Replace line 61: "A profile named "{trimmed}" already exists."
master_store = master_store.replace(
    'return $"A profile named "{trimmed}" already exists.";',
    'return $"A profile named \\u201C{trimmed}\\u201D already exists.";'
)

# Replace line 77: "A profile named "{trimmed}" already exists." (same but different context)
# This needs to be in the rename function
import re
lines = master_store.split('\n')
for i, line in enumerate(lines):
    if 'public string? RenameActiveProfile' in line:
        # Find the next occurrence within this function
        for j in range(i, min(i+15, len(lines))):
            if 'return $"A profile named' in lines[j] and '\\u201C' not in lines[j]:
                lines[j] = lines[j].replace(
                    'return $"A profile named "{trimmed}" already exists.";',
                    'return $"A profile named \\u201C{trimmed}\\u201D already exists.";'
                )
                break

master_store = '\n'.join(lines)

# Replace line 92: "Can't delete the last profile."
master_store = master_store.replace(
    'return "Can\'t delete the last profile.";',
    'return "Can\\u2019t delete the last profile.";'
)

# Replace line 103: No profile named "{name}".
master_store = master_store.replace(
    'return $"No profile named "{name}".";',
    'return $"No profile named \\u201C{name}\\u201D.";'
)

with open("windows/src/ConnectorControl.Core/MasterStore.cs", "w", encoding="utf-8") as f:
    f.write(master_store)

# ProfileTests.cs
profile_tests = open("windows/tests/ConnectorControl.Core.Tests/ProfileTests.cs", "r", encoding="utf-8").read()

# Line 116
profile_tests = profile_tests.replace(
    'Assert.Equal("A profile named "Default" already exists.",',
    'Assert.Equal("A profile named \\u201CDefault\\u201D already exists.",'
)

# Line 132
profile_tests = profile_tests.replace(
    'Assert.Equal("A profile named "Personal" already exists.",',
    'Assert.Equal("A profile named \\u201CPersonal\\u201D already exists.",'
)

# Line 155
profile_tests = profile_tests.replace(
    'Assert.Equal("Can\'t delete the last profile.",',
    'Assert.Equal("Can\\u2019t delete the last profile.",'
)

# Line 171
profile_tests = profile_tests.replace(
    'Assert.Equal("No profile named "Nope".",',
    'Assert.Equal("No profile named \\u201CNope\\u201D.",'
)

with open("windows/tests/ConnectorControl.Core.Tests/ProfileTests.cs", "w", encoding="utf-8") as f:
    f.write(profile_tests)

print("Fixed!")
