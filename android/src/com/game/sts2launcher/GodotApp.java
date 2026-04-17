package com.game.sts2launcher;

import org.godotengine.godot.Godot;
import org.godotengine.godot.GodotActivity;

import android.content.Intent;
import android.os.Bundle;
import android.util.Log;
import android.view.KeyEvent;
import android.widget.Toast;

import androidx.activity.EdgeToEdge;
import androidx.core.splashscreen.SplashScreen;

import android.content.SharedPreferences;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.KeyStore;
import java.util.ArrayList;
import java.util.List;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.util.Base64;

import org.fmod.FMOD;

// Main activity for the mobile launcher. Handles FMOD initialization, .NET assembly
// setup, PCK loading, LAN multicast, and Android Keystore encryption for credentials.
public class GodotApp extends GodotActivity {
	static {
		// FMOD must load before Godot's GDExtension or FMOD_JNI_GetEnv fails.
		System.loadLibrary("fmod");
		System.loadLibrary("fmodstudio");
		// Required for TLS/SSL (SteamKit2 WebSocket, HTTPS).
		System.loadLibrary("System.Security.Cryptography.Native.Android");
	}

	private static GodotApp instance;
	private WifiManager.MulticastLock multicastLock;
	private String gameDir;
	private static final String TAG = "STS2Mobile";
	private static final String KEYSTORE_ALIAS = "sts2mobile_credentials";
	private static final String PCK_FILE = "SlayTheSpire2.pck";
	private static final int REQ_SAF_ZIP = 4201;

	private volatile boolean pickerActive = false;
	private final java.util.List<String> lastPickedZipPaths =
			java.util.Collections.synchronizedList(new java.util.ArrayList<>());

	private final Runnable updateWindowAppearance = () -> {
		Godot godot = getGodot();
		if (godot != null) {
			godot.enableImmersiveMode(true, true);
			godot.enableEdgeToEdge(true, true);
			godot.setSystemBarsAppearance();
		}
	};

	@Override
	public void onCreate(Bundle savedInstanceState) {
		instance = this;
		gameDir = new File(getFilesDir(), "game").getAbsolutePath();

		SplashScreen.installSplashScreen(this);
		EdgeToEdge.enable(this);

		// Must be called before any native FMOD calls.
		FMOD.init(this);

		setupAssemblies();
		extractAssetFile("FMOD_LOGOS/FMOD Logo White - Transparent Background.png", "fmod_logo.png");

		super.onCreate(savedInstanceState);

		// Android WiFi power saving drops broadcast packets without a MulticastLock.
		try {
			WifiManager wifiMgr = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
			multicastLock = wifiMgr.createMulticastLock("sts2_lan_discovery");
			multicastLock.setReferenceCounted(false);
			multicastLock.acquire();
			Log.i(TAG, "WiFi MulticastLock acquired for LAN discovery");
		} catch (Exception e) {
			Log.w(TAG, "Failed to acquire MulticastLock", e);
		}
	}

	private boolean isNewVersion() {
		SharedPreferences prefs = getSharedPreferences("sts2mobile", MODE_PRIVATE);
		int lastVersion = prefs.getInt("installed_version_code", -1);
		int currentVersion = BuildConfig.VERSION_CODE;
		if (lastVersion == currentVersion) {
			return false;
		}
		Log.i(TAG, "Version changed: " + lastVersion + " -> " + currentVersion);
		prefs.edit().putInt("installed_version_code", currentVersion).apply();
		return true;
	}

	// Copies .NET BCL from APK assets and game assemblies from the download
	// directory
	// into the location Godot expects. Skips if already done unless the APK version
	// changed.
	private void setupAssemblies() {
		File srcDir = findAssembliesDir();
		File destDir = new File(getFilesDir(), ".godot/mono/publish/arm64");

		boolean versionChanged = isNewVersion();

		File patcherMarker = new File(destDir, "STS2Mobile.dll");
		File sts2Marker = new File(destDir, "sts2.dll");
		if (sts2Marker.exists() && patcherMarker.exists() && !versionChanged) {
			Log.i(TAG, "Assemblies already set up at: " + destDir.getAbsolutePath());
			return;
		}

		if (versionChanged) {
			Log.i(TAG, "New version detected, re-copying all assemblies");
		}

		destDir.mkdirs();

		try {
			String[] bclFiles = getAssets().list("dotnet_bcl");
			if (bclFiles != null) {
				int count = 0;
				for (String name : bclFiles) {
					try (InputStream in = getAssets().open("dotnet_bcl/" + name);
							OutputStream out = new FileOutputStream(new File(destDir, name))) {
						byte[] buf = new byte[8192];
						int len;
						while ((len = in.read(buf)) > 0) {
							out.write(buf, 0, len);
						}
						count++;
					}
				}
				Log.i(TAG, "Copied " + count + " BCL assemblies from assets");
			}
		} catch (IOException e) {
			Log.e(TAG, "Failed to copy BCL assemblies", e);
		}

		// Only copy game assemblies that don't already exist in BCL. The depot has
		// desktop
		// CoreCLR versions that are incompatible with Android's Mono runtime.
		if (!srcDir.exists() || !srcDir.isDirectory()) {
			Log.w(TAG, "Game assemblies source dir not found: " + srcDir.getAbsolutePath());
			return;
		}

		Log.i(TAG, "Copying game assemblies from " + srcDir + " to " + destDir);
		File[] files = srcDir.listFiles();
		if (files == null)
			return;

		int count = 0;
		for (File src : files) {
			if (src.isFile()) {
				String name = src.getName();
				if (name.endsWith(".so")) {
					continue;
				}
				File dest = new File(destDir, name);
				if (dest.exists()) {
					continue;
				}
				try {
					copyFile(src, dest);
					count++;
				} catch (IOException e) {
					Log.e(TAG, "Failed to copy: " + name, e);
				}
			}
		}
		Log.i(TAG, "Copied " + count + " game assembly files");
	}

	private File findAssembliesDir() {
		File gameDirFile = new File(gameDir);
		if (gameDirFile.exists() && gameDirFile.isDirectory()) {
			File[] children = gameDirFile.listFiles();
			if (children != null) {
				for (File child : children) {
					if (child.isDirectory() && child.getName().startsWith("data_")) {
						Log.i(TAG, "Found assemblies dir: " + child.getName());
						return child;
					}
				}
			}
		}
		return new File(gameDir, "data_sts2_windows_x86_64");
	}

	private void copyFile(File src, File dest) throws IOException {
		try (InputStream in = new FileInputStream(src);
				OutputStream out = new FileOutputStream(dest)) {
			byte[] buf = new byte[8192];
			int len;
			while ((len = in.read(buf)) > 0) {
				out.write(buf, 0, len);
			}
		}
	}

	// Extracts a single file from APK assets to the files directory.
	private void extractAssetFile(String assetPath, String destName) {
		File dest = new File(getFilesDir(), destName);
		if (dest.exists())
			return;
		try (InputStream in = getAssets().open(assetPath);
				OutputStream out = new FileOutputStream(dest)) {
			byte[] buf = new byte[4096];
			int len;
			while ((len = in.read(buf)) > 0) {
				out.write(buf, 0, len);
			}
		} catch (IOException e) {
			Log.w(TAG, "Failed to extract " + assetPath, e);
		}
	}

	@Override
	public List<String> getCommandLine() {
		List<String> commands = new ArrayList<>(super.getCommandLine());
		File pckFile = new File(gameDir, PCK_FILE);
		if (pckFile.exists()) {
			commands.add("--main-pack");
			commands.add(pckFile.getAbsolutePath());
			Log.i(TAG, "Loading PCK from: " + pckFile.getAbsolutePath());
		} else {
			// No game files yet; use bootstrap PCK so Godot can initialize for the
			// launcher.
			String bootstrapPck = extractBootstrapPck();
			if (bootstrapPck != null) {
				commands.add("--main-pack");
				commands.add(bootstrapPck);
				Log.i(TAG, "Using bootstrap PCK for launcher-only mode");
			}
		}
		return commands;
	}

	private String extractBootstrapPck() {
		File dest = new File(getFilesDir(), "bootstrap.pck");
		if (dest.exists()) {
			return dest.getAbsolutePath();
		}
		try (InputStream in = getAssets().open("bootstrap.pck");
				OutputStream out = new FileOutputStream(dest)) {
			byte[] buf = new byte[4096];
			int len;
			while ((len = in.read(buf)) > 0) {
				out.write(buf, 0, len);
			}
			return dest.getAbsolutePath();
		} catch (IOException e) {
			Log.e(TAG, "Failed to extract bootstrap PCK", e);
			return null;
		}
	}

	@Override
	public void onResume() {
		super.onResume();
		updateWindowAppearance.run();
	}

	private long lastBackPressTimeMs = 0L;
	private Toast lastBackPressToast;
	private static final long BACK_PRESS_CONFIRM_WINDOW_MS = 2000L;

	// Intercept the hardware back button before Godot's render view swallows it.
	// First press shows a toast and is discarded; a second press within 2s is
	// allowed through so NGame.Quit (and therefore restartApp()) runs. Prevents
	// accidentally dropping an in-progress run with a stray swipe.
	@Override
	public boolean dispatchKeyEvent(KeyEvent event) {
		if (event.getKeyCode() != KeyEvent.KEYCODE_BACK) {
			return super.dispatchKeyEvent(event);
		}
		long now = System.currentTimeMillis();
		boolean withinWindow = (now - lastBackPressTimeMs) < BACK_PRESS_CONFIRM_WINDOW_MS;

		if (event.getAction() == KeyEvent.ACTION_DOWN) {
			// Second press: pass DOWN through so Godot receives the full pair.
			// First press: swallow DOWN so the render view cannot act on it.
			return withinWindow ? super.dispatchKeyEvent(event) : true;
		}
		if (event.getAction() == KeyEvent.ACTION_UP) {
			if (withinWindow) {
				if (lastBackPressToast != null) {
					lastBackPressToast.cancel();
					lastBackPressToast = null;
				}
				lastBackPressTimeMs = 0L;
				return super.dispatchKeyEvent(event);
			}
			lastBackPressTimeMs = now;
			if (lastBackPressToast != null) {
				lastBackPressToast.cancel();
			}
			lastBackPressToast = Toast.makeText(
				this,
				"Press back again to exit",
				Toast.LENGTH_SHORT
			);
			lastBackPressToast.show();
			return true;
		}
		return super.dispatchKeyEvent(event);
	}

	@Override
	public void onGodotMainLoopStarted() {
		super.onGodotMainLoopStarted();
		runOnUiThread(updateWindowAppearance);
	}

	@Override
	protected void onDestroy() {
		if (multicastLock != null && multicastLock.isHeld()) {
			multicastLock.release();
			Log.i(TAG, "WiFi MulticastLock released");
		}
		FMOD.close();
		super.onDestroy();
	}

	public static GodotApp getInstance() {
		return instance;
	}

	public String getGameDir() {
		return gameDir;
	}

	public String getVersionName() {
		return BuildConfig.VERSION_NAME;
	}

	public void restartApp() {
		Log.i(TAG, "Restarting app...");
		Intent intent = getPackageManager().getLaunchIntentForPackage(getPackageName());
		if (intent != null) {
			intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
			startActivity(intent);
		}
		Runtime.getRuntime().exit(0);
	}

	// AES-256-GCM encryption via Android Keystore (hardware-backed TEE).
	private SecretKey getOrCreateKeystoreKey() throws Exception {
		KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
		keyStore.load(null);

		if (keyStore.containsAlias(KEYSTORE_ALIAS)) {
			return ((KeyStore.SecretKeyEntry) keyStore.getEntry(KEYSTORE_ALIAS, null)).getSecretKey();
		}

		KeyGenerator keyGen = KeyGenerator.getInstance(
				android.security.keystore.KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
		keyGen.init(new android.security.keystore.KeyGenParameterSpec.Builder(
				KEYSTORE_ALIAS,
				android.security.keystore.KeyProperties.PURPOSE_ENCRYPT
						| android.security.keystore.KeyProperties.PURPOSE_DECRYPT)
				.setBlockModes(android.security.keystore.KeyProperties.BLOCK_MODE_GCM)
				.setEncryptionPaddings(android.security.keystore.KeyProperties.ENCRYPTION_PADDING_NONE)
				.setKeySize(256)
				.build());
		return keyGen.generateKey();
	}

	public String encryptString(String plaintext) {
		try {
			SecretKey key = getOrCreateKeystoreKey();
			Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
			cipher.init(Cipher.ENCRYPT_MODE, key);
			byte[] iv = cipher.getIV();
			byte[] ciphertext = cipher.doFinal(plaintext.getBytes("UTF-8"));

			// Format: [iv_length (1 byte)] [iv] [ciphertext]
			byte[] result = new byte[1 + iv.length + ciphertext.length];
			result[0] = (byte) iv.length;
			System.arraycopy(iv, 0, result, 1, iv.length);
			System.arraycopy(ciphertext, 0, result, 1 + iv.length, ciphertext.length);
			return Base64.encodeToString(result, Base64.NO_WRAP);
		} catch (Exception e) {
			Log.e(TAG, "Encryption failed", e);
			return null;
		}
	}

	public String decryptString(String encrypted) {
		try {
			byte[] blob = Base64.decode(encrypted, Base64.NO_WRAP);
			int ivLength = blob[0] & 0xFF;
			byte[] iv = new byte[ivLength];
			System.arraycopy(blob, 1, iv, 0, ivLength);
			byte[] ciphertext = new byte[blob.length - 1 - ivLength];
			System.arraycopy(blob, 1 + ivLength, ciphertext, 0, ciphertext.length);

			SecretKey key = getOrCreateKeystoreKey();
			Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
			cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(128, iv));
			byte[] plaintext = cipher.doFinal(ciphertext);
			return new String(plaintext, "UTF-8");
		} catch (Exception e) {
			Log.e(TAG, "Decryption failed", e);
			return null;
		}
	}

	// Returns true if the app has permission to write to shared external storage.
	public boolean hasStoragePermission() {
		if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
			return android.os.Environment.isExternalStorageManager();
		}
		return checkSelfPermission(
				android.Manifest.permission.WRITE_EXTERNAL_STORAGE) == android.content.pm.PackageManager.PERMISSION_GRANTED;
	}

	// Requests external storage permission. On Android 11+, opens the system
	// settings
	// page for "All files access". On older versions, shows the runtime permission
	// dialog.
	public void requestStoragePermission() {
		if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
			try {
				Intent intent = new Intent(android.provider.Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION);
				intent.setData(android.net.Uri.parse("package:" + getPackageName()));
				startActivity(intent);
			} catch (Exception e) {
				Log.w(TAG, "Failed to open app-specific storage settings, trying general", e);
				Intent intent = new Intent(android.provider.Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION);
				startActivity(intent);
			}
		} else {
			requestPermissions(new String[] { android.Manifest.permission.WRITE_EXTERNAL_STORAGE }, 1);
		}
	}

	// Opens the system document picker (SAF) so the user can select one or more
	// mod zips from any provider the device exposes (Downloads, Drive, etc.).
	// The result is handled in onActivityResult; each picked file is copied into
	// the app cache, and the absolute paths are drained by C# via
	// consumePickedZipPaths().
	public void openZipPicker() {
		Log.i(TAG, "[Mods] openZipPicker invoked from C#");
		pickerActive = true;
		lastPickedZipPaths.clear();
		runOnUiThread(() -> {
			try {
				Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
				intent.addCategory(Intent.CATEGORY_OPENABLE);
				intent.setType("*/*");
				intent.putExtra(Intent.EXTRA_MIME_TYPES, new String[] {
						"application/zip",
						"application/x-zip-compressed",
						"application/octet-stream"
				});
				intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
				Log.i(TAG, "[Mods] Starting SAF ACTION_OPEN_DOCUMENT intent (multi)");
				startActivityForResult(intent, REQ_SAF_ZIP);
				Log.i(TAG, "[Mods] startActivityForResult returned");
			} catch (Exception e) {
				Log.e(TAG, "[Mods] Failed to start zip picker", e);
				pickerActive = false;
			}
		});
	}

	public boolean isPickerActive() {
		return pickerActive;
	}

	// Returns all copied zip paths as a single newline-separated string and clears
	// the buffer. Returns empty string when the user cancelled or nothing was picked.
	public String consumePickedZipPaths() {
		synchronized (lastPickedZipPaths) {
			if (lastPickedZipPaths.isEmpty())
				return "";
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < lastPickedZipPaths.size(); i++) {
				if (i > 0) sb.append('\n');
				sb.append(lastPickedZipPaths.get(i));
			}
			lastPickedZipPaths.clear();
			return sb.toString();
		}
	}

	// Kept for backward compatibility with any caller that still grabs a single path.
	public String consumeLastPickedZipPath() {
		synchronized (lastPickedZipPaths) {
			if (lastPickedZipPaths.isEmpty())
				return null;
			String path = lastPickedZipPaths.remove(0);
			return path;
		}
	}

	@Override
	protected void onActivityResult(int requestCode, int resultCode, Intent data) {
		super.onActivityResult(requestCode, resultCode, data);
		Log.i(TAG, "[Mods] onActivityResult requestCode=" + requestCode + " resultCode=" + resultCode);
		if (requestCode != REQ_SAF_ZIP) {
			return;
		}
		try {
			if (resultCode == RESULT_OK && data != null) {
				java.util.List<android.net.Uri> uris = new java.util.ArrayList<>();
				android.content.ClipData clip = data.getClipData();
				if (clip != null) {
					for (int i = 0; i < clip.getItemCount(); i++) {
						android.net.Uri u = clip.getItemAt(i).getUri();
						if (u != null) uris.add(u);
					}
				} else if (data.getData() != null) {
					uris.add(data.getData());
				}
				Log.i(TAG, "[Mods] Picked " + uris.size() + " file(s)");

				long ts = System.currentTimeMillis();
				for (int i = 0; i < uris.size(); i++) {
					android.net.Uri uri = uris.get(i);
					File dest = new File(getCacheDir(), "mod_import_" + ts + "_" + i + ".zip");
					try (InputStream in = getContentResolver().openInputStream(uri);
							FileOutputStream out = new FileOutputStream(dest)) {
						byte[] buf = new byte[16384];
						int len;
						while ((len = in.read(buf)) > 0) {
							out.write(buf, 0, len);
						}
					}
					lastPickedZipPaths.add(dest.getAbsolutePath());
					Log.i(TAG, "[Mods] Mod zip copied to: " + dest.getAbsolutePath());
				}
			}
		} catch (Exception e) {
			Log.e(TAG, "Failed to read picked zip(s)", e);
		} finally {
			pickerActive = false;
		}
	}

	public void deleteKeystoreKey() {
		try {
			KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
			keyStore.load(null);
			keyStore.deleteEntry(KEYSTORE_ALIAS);
		} catch (Exception e) {
			Log.e(TAG, "Failed to delete keystore key", e);
		}
	}
}
