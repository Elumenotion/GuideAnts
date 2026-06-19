"""
Storage module for presentation persistence.
Handles file I/O and path management for presentation JSON files.

Uses file locking to prevent race conditions when multiple operations
run in parallel on the same presentation.
"""

import os
import json
import uuid
import time
import contextlib
from datetime import datetime
from pathlib import Path

# Try to import filelock for cross-platform locking
try:
    from filelock import FileLock, Timeout
    _HAS_FILELOCK = True
except ImportError:
    _HAS_FILELOCK = False
    # Simple fallback lock implementation
    class Timeout(Exception):
        pass


# Module directory detection
_MODULE_DIR = os.path.dirname(os.path.abspath(__file__))
STORAGE_DIR = os.path.join(_MODULE_DIR, "presentations")
INDEX_FILE = os.path.join(STORAGE_DIR, ".index.json")

# Lock timeout in seconds
LOCK_TIMEOUT = 30

# In-memory cache for presentations being modified
_presentation_locks = {}


def _get_lock_path(presentation_id):
    """Get the lock file path for a presentation."""
    ensure_storage_dir()
    return os.path.join(STORAGE_DIR, f".{presentation_id}.lock")


def _get_index_lock_path():
    """Get the lock file path for the index."""
    ensure_storage_dir()
    return os.path.join(STORAGE_DIR, ".index.lock")


@contextlib.contextmanager
def _file_lock(lock_path, timeout=LOCK_TIMEOUT):
    """
    Cross-platform file lock context manager.
    
    Uses filelock library if available, otherwise uses a simple
    lock file with retry mechanism.
    
    Parallel callers will BLOCK (wait) until the lock is available,
    up to the timeout. This ensures all parallel operations eventually
    succeed (as long as each completes within timeout).
    """
    if _HAS_FILELOCK:
        lock = FileLock(lock_path, timeout=timeout)
        # Acquire lock - this blocks until available or timeout
        try:
            lock.acquire(timeout=timeout)
        except Timeout:
            raise TimeoutError(f"Could not acquire lock on {lock_path} within {timeout}s - too many concurrent operations")
        # Lock acquired - now yield and ensure release
        try:
            yield
        finally:
            lock.release()
    else:
        # Simple fallback: create lock file with retries
        start_time = time.time()
        acquired = False
        
        while time.time() - start_time < timeout:
            try:
                # Try to create lock file exclusively
                fd = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.write(fd, str(os.getpid()).encode())
                os.close(fd)
                acquired = True
                break
            except FileExistsError:
                # Lock file exists, check if it's stale (older than timeout)
                try:
                    lock_age = time.time() - os.path.getmtime(lock_path)
                    if lock_age > timeout:
                        # Stale lock, remove it
                        os.remove(lock_path)
                        continue
                except OSError:
                    pass
                # Wait and retry
                time.sleep(0.1)
        
        if not acquired:
            raise TimeoutError(f"Could not acquire lock on {lock_path} within {timeout}s - too many concurrent operations")
        
        try:
            yield
        finally:
            try:
                os.remove(lock_path)
            except OSError:
                pass


@contextlib.contextmanager
def presentation_lock(presentation_id, timeout=LOCK_TIMEOUT):
    """
    Context manager for exclusive access to a presentation.
    
    Use this when you need to read, modify, and write a presentation
    to prevent race conditions with parallel operations.
    
    Example:
        with presentation_lock(presentation_id):
            pres = load_presentation(presentation_id)
            pres['slides'].append(new_slide)
            save_presentation(pres)
    """
    lock_path = _get_lock_path(presentation_id)
    with _file_lock(lock_path, timeout):
        yield


# Thread-local storage to track if we already hold the index lock
import threading
_index_lock_holder = threading.local()


@contextlib.contextmanager
def index_lock(timeout=LOCK_TIMEOUT):
    """
    Context manager for exclusive access to the index.
    Re-entrant: if the same thread already holds the lock, just yield.
    """
    # Check if we already hold the lock (re-entrant call)
    if getattr(_index_lock_holder, 'held', False):
        yield
        return
    
    lock_path = _get_index_lock_path()
    with _file_lock(lock_path, timeout):
        _index_lock_holder.held = True
        try:
            yield
        finally:
            _index_lock_holder.held = False


def ensure_storage_dir():
    """Ensure the storage directory exists."""
    os.makedirs(STORAGE_DIR, exist_ok=True)


def get_presentation_file(presentation_id):
    """Get the full path to a presentation JSON file."""
    ensure_storage_dir()
    return os.path.join(STORAGE_DIR, f"{presentation_id}.json")


def load_presentation(presentation_id):
    """Load a presentation from storage."""
    file_path = get_presentation_file(presentation_id)
    if not os.path.exists(file_path):
        raise FileNotFoundError(f"Presentation {presentation_id} not found")
    
    with open(file_path, 'r', encoding='utf-8') as f:
        return json.load(f)


def save_presentation(presentation):
    """Save a presentation to storage.
    
    Note: This function assumes the caller holds the presentation_lock.
    It writes directly to the file (no temp file) since locking prevents
    concurrent access.
    """
    ensure_storage_dir()
    presentation['updated_at'] = datetime.utcnow().isoformat() + 'Z'
    
    file_path = get_presentation_file(presentation['id'])
    
    # Direct write - caller must hold presentation_lock to prevent races
    with open(file_path, 'w', encoding='utf-8') as f:
        json.dump(presentation, f, indent=2, ensure_ascii=False)
    
    # Update index
    update_index(presentation['id'], presentation['name'], presentation['updated_at'])


def delete_presentation_file(presentation_id):
    """Delete a presentation file."""
    file_path = get_presentation_file(presentation_id)
    if os.path.exists(file_path):
        os.remove(file_path)
        remove_from_index(presentation_id)


def create_new_presentation(name, initial_content=None):
    """Create a new presentation structure."""
    presentation_id = str(uuid.uuid4())
    now = datetime.utcnow().isoformat() + 'Z'
    
    slides = []
    if initial_content:
        # Parse initial content into slides if provided
        from .markdown_ops import parse_markdown_to_slides
        slides = parse_markdown_to_slides(initial_content)
    
    presentation = {
        "id": presentation_id,
        "name": name,
        "created_at": now,
        "updated_at": now,
        "slides": slides
    }
    
    save_presentation(presentation)
    return presentation


def load_index():
    """Load the presentations index."""
    ensure_storage_dir()
    if not os.path.exists(INDEX_FILE):
        return {"presentations": []}
    
    try:
        with open(INDEX_FILE, 'r', encoding='utf-8') as f:
            return json.load(f)
    except (json.JSONDecodeError, IOError):
        return {"presentations": []}


def save_index(index_data):
    """Save the presentations index.
    
    Note: This function assumes the caller holds the index_lock.
    It writes directly to the file (no temp file) since locking prevents
    concurrent access.
    """
    ensure_storage_dir()
    # Direct write - caller must hold index_lock to prevent races
    with open(INDEX_FILE, 'w', encoding='utf-8') as f:
        json.dump(index_data, f, indent=2, ensure_ascii=False)


def update_index(presentation_id, name, updated_at):
    """Update or add entry in index."""
    with index_lock():
        index = load_index()
        presentations = index.get("presentations", [])
        
        # Find existing entry
        found = False
        for p in presentations:
            if p["id"] == presentation_id:
                p["name"] = name
                p["updated_at"] = updated_at
                found = True
                break
        
        if not found:
            presentations.append({
                "id": presentation_id,
                "name": name,
                "file": f"{presentation_id}.json",
                "updated_at": updated_at
            })
        
        index["presentations"] = presentations
        save_index(index)


def remove_from_index(presentation_id):
    """Remove entry from index."""
    with index_lock():
        index = load_index()
        presentations = index.get("presentations", [])
        index["presentations"] = [p for p in presentations if p["id"] != presentation_id]
        save_index(index)


def get_all_presentation_ids():
    """Get all presentation IDs from index."""
    index = load_index()
    return [p["id"] for p in index.get("presentations", [])]


def _sanitize_name_for_comparison(name):
    """Sanitize a name for comparison - replaces special chars with underscores."""
    import re
    return re.sub(r'[<>:"/\\|?*]', '_', name)


def find_presentation_by_name(name):
    """
    Find a presentation by name (matches both exact and sanitized names).
    
    This prevents duplicate presentations when names differ only by 
    special characters (e.g., "Title: Subtitle" vs "Title_ Subtitle").
    
    Args:
        name: Presentation name to search for
        
    Returns:
        str or None: Presentation ID if found, None otherwise
    """
    index = load_index()
    presentations = index.get("presentations", [])
    
    # First try exact match
    for p in presentations:
        if p.get("name") == name:
            return p.get("id")
    
    # Then try sanitized match (handles "Title: X" matching "Title_ X")
    sanitized_search = _sanitize_name_for_comparison(name)
    for p in presentations:
        existing_name = p.get("name", "")
        sanitized_existing = _sanitize_name_for_comparison(existing_name)
        if sanitized_existing == sanitized_search:
            return p.get("id")
    
    return None


def get_presentation_by_name(name):
    """
    Get a presentation by name (case-sensitive exact match).
    
    Args:
        name: Presentation name to search for
        
    Returns:
        dict: Presentation dictionary if found
        
    Raises:
        FileNotFoundError: If presentation with the name doesn't exist
    """
    presentation_id = find_presentation_by_name(name)
    if presentation_id is None:
        raise FileNotFoundError(f"Presentation with name '{name}' not found")
    return load_presentation(presentation_id)