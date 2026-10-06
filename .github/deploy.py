import os
import sys
import time
import io
from ftplib import FTP, error_perm

FTP_SERVER = os.environ.get("FTP_SERVER")
FTP_USERNAME = os.environ.get("FTP_USERNAME")
FTP_PASSWORD = os.environ.get("FTP_PASSWORD")
REMOTE_DIR = os.environ.get("FTP_REMOTE_DIR", "/wwwroot")

if not FTP_SERVER or not FTP_USERNAME or not FTP_PASSWORD:
    print("[ERROR] Missing FTP credentials in environment variables (FTP_SERVER, FTP_USERNAME, FTP_PASSWORD).")
    sys.exit(1)

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PUBLISH_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "backend", "publish"))

if not os.path.exists(PUBLISH_DIR):
    print(f"[ERROR] Publish directory not found at {PUBLISH_DIR}.")
    sys.exit(1)

def should_skip_file(rel_path: str) -> bool:
    normalized = rel_path.replace("\\", "/").lower()
    if "wwwroot/quotes/q-" in normalized:
        return True
    return False

def upload_directory(ftp: FTP, local_dir: str, remote_dir: str):
    print(f"\n[FTP] Syncing directory: {local_dir} -> {remote_dir}")
    try:
        ftp.mkd(remote_dir)
    except error_perm:
        pass

    ftp.cwd(remote_dir)

    for item in sorted(os.listdir(local_dir)):
        local_path = os.path.join(local_dir, item)
        rel_path = os.path.relpath(local_path, PUBLISH_DIR)

        if os.path.isdir(local_path):
            upload_directory(ftp, local_path, f"{remote_dir}/{item}")
            ftp.cwd(remote_dir)
        else:
            if should_skip_file(rel_path):
                continue

            file_size = os.path.getsize(local_path)
            max_retries = 3
            for attempt in range(1, max_retries + 1):
                try:
                    print(f"  Uploading: {item} ({file_size:,} bytes)...")
                    with open(local_path, "rb") as f:
                        ftp.storbinary(f"STOR {item}", f)
                    break
                except Exception as ex:
                    print(f"    [WARN] Attempt {attempt} failed for {item}: {ex}")
                    if attempt == max_retries:
                        raise
                    time.sleep(2)

def main():
    print("=== MonsterASP.NET FTP Deployment ===")
    print(f"Local Publish Dir: {PUBLISH_DIR}")
    print(f"Target Host:       {FTP_SERVER}")
    print(f"Target User:       {FTP_USERNAME}")
    print(f"Remote Dir:        {REMOTE_DIR}")

    total_files = sum(1 for root, _, files in os.walk(PUBLISH_DIR) for f in files if not should_skip_file(os.path.relpath(os.path.join(root, f), PUBLISH_DIR)))
    total_size = sum(os.path.getsize(os.path.join(root, f)) for root, _, files in os.walk(PUBLISH_DIR) for f in files if not should_skip_file(os.path.relpath(os.path.join(root, f), PUBLISH_DIR)))
    print(f"Ready to deploy {total_files} production files ({total_size / (1024*1024):.2f} MB).\n")

    print(f"Connecting to FTP server {FTP_SERVER}...")
    with FTP(FTP_SERVER, FTP_USERNAME, FTP_PASSWORD, timeout=60) as ftp:
        print(f"Connected successfully! Server welcome: {ftp.getwelcome().strip()}")

        print("\n[STEP 1] Taking app offline temporarily to release IIS file locks...")
        try:
            ftp.cwd(REMOTE_DIR)
        except error_perm:
            ftp.cwd("/")
            try:
                ftp.mkd(REMOTE_DIR.lstrip("/"))
            except error_perm:
                pass
            ftp.cwd(REMOTE_DIR)

        offline_html = b"<!DOCTYPE html><html><head><title>Updating Application</title></head><body><h2>Updating System... Please wait a few seconds.</h2></body></html>"
        ftp.storbinary("STOR app_offline.htm", io.BytesIO(offline_html))
        print("  Uploaded app_offline.htm. Waiting 5 seconds for IIS to release file locks...")
        time.sleep(5)

        print("\n[STEP 2] Uploading published application files...")
        upload_directory(ftp, PUBLISH_DIR, REMOTE_DIR)

        print("\n[STEP 3] Bringing app back online...")
        ftp.cwd(REMOTE_DIR)
        try:
            ftp.delete("app_offline.htm")
            print("  Removed app_offline.htm successfully. App is now live!")
        except Exception as e:
            print(f"  [Notice] Could not delete app_offline.htm: {e}")

        print("\n[SUCCESS] MonsterASP.NET deployment completed successfully!")

if __name__ == "__main__":
    main()
