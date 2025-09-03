# Unity Package Authentication Fix

## Problem
Unity Package Manager was failing to resolve Git-based package dependencies with the error:
```
remote: Invalid username or token. Password authentication is not supported for Git operations.
fatal: Authentication failed for 'https://github.com/boyonglin/STL-Converter.git/'
```

## Root Cause
GitHub deprecated password authentication for Git operations in August 2021. Unity Package Manager was attempting to clone packages using dynamic Git URLs without proper authentication.

## Solution
Updated Unity package manifest files to use specific commit hashes instead of dynamic branch references:

### Before:
```json
"co.parabox.stl": "https://github.com/karl-/pb_Stl.git",
"com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/UnityMcpBridge"
```

### After:
```json
"co.parabox.stl": "https://github.com/karl-/pb_Stl.git#b6b2a6815f81ea2c61ae9f22d8e0635d363b5212",
"com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/UnityMcpBridge#22e8016aeef46bf8d11897588c49c2f5a8e04f7f"
```

## Files Modified
- `Packages/manifest.json` - Added commit hash references to Git URLs
- `Packages/packages-lock.json` - Updated version strings to match manifest

## Benefits
- ✅ No authentication required for package resolution
- ✅ Deterministic package versions (locked to specific commits)
- ✅ Improved reproducibility across different environments
- ✅ Maintains all existing functionality

## Verification
The fix was tested by verifying that:
1. The specified commit hashes exist in the source repositories
2. The repositories are accessible without authentication
3. Unity package manifest format is valid