# Digital Business Capstone Project

This project redefines human interaction in virtual spaces, merging emotion, context, and communication into one seamless experience. Designed for the Meta headset, it serves as a voice-enabled virtual chatbot for The Public Speaking Project, helping users master performance, manage stress, and handle their presence in front of any audience. 

## 1. Members

| Name              | Background                |
|-------------------|-----------------------|
| Camille Bouvier   | Business Engineering |
| Renaud Cornélis   | Business Engineering |
| William Dubuisson | Business Engineering |
| Antoine Grosjean  | Computer Science & Engineering |
| Louis Hogge       | Computer Science & Engineering |
| Sélim Kadioglu    | Computer Science & Engineering |
| Antoine Stasse    | Business Engineering |

## 2. Directory Overview

- LLM : All LLM related work.
- S2T : All S2T related work.
- T2S : All T2S related work.

## 3. Project Status
The project is under development.

## 4. Git LFS and Large Files

This repository uses **Git LFS (Large File Storage)** to manage heavy binary files:

- `*.onnx` – model files
- `*.dll` – Windows native libraries
- `*.so` – Linux/Android native libraries

These patterns are defined in the `.gitattributes` file and must be handled via Git LFS.

### 4.1. Initial setup (one time per machine)

On each machine that will clone and work on this repo, install and initialize Git LFS:

```bash
git lfs install
```

Then clone the repository normally (do **not** use "Download ZIP"):

```bash
git clone https://github.com/<org-or-user>/dbcp.git
cd dbcp
```

Git LFS will automatically pull the large files during the clone. If needed, you can force an update with:

```bash
git lfs pull
```

### 4.2. Adding or updating large files

When you add or modify `.onnx`, `.dll`, or `.so` files, they will automatically be stored via Git LFS because of the existing `.gitattributes` configuration. Just stage and commit them as usual:

```bash
git add .
git commit -m "Update models and native libraries"
git push
```

No extra Git LFS commands are required unless you introduce new large file types (e.g., `*.bin`, `*.pt`), in which case you should track them via:

```bash
git lfs track "*.pt"
```

and commit the updated `.gitattributes`.

## 5. License
Project realised in the context of the Digital Business Capstone Project GEST7064-1 course at HEC Liège.