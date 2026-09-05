# Build instructions
In general please follow the build instructions as described in [README.md](README.md). Here you will find a brief overview about what is needed to build your own development environment without further explanations.

## Setup on macOS
### Prerequisites
* install supported python version (see [README.md](README.md))
* install python extension compile environment, this is automatically done if you have xcode
* install git
### Clone the repository
```
git clone <this repository>
cd PetBottleFirament
git pull
```
### Install and activate the virtual environment
```
python3 -m venv venv
. ./venv/bin/activate
```
### Install and update all required libraries
```
pip install --upgrade pip
pip install --upgrade setuptools
pip install -r requirements.txt
pip install cython
python setup.py build_ext --inplace
```

### For running
`python petfil.py`

### For packaging
PyInstaller is used for packaging; see [release_windows.bat](release_windows.bat) for the equivalent automated build on Windows.

## Setup on Windows
### Prerequisites
* install supported python version (see [README.md](README.md))
* install python extension compile environment, see https://wiki.python.org/moin/WindowsCompilers
* install git
### Clone the repository
```
git clone <this repository>
cd PetBottleFirament
git pull
```
### Install and activate the virtual environment
```
\path\to\python3\python -m venv v3
v3\Scripts\activate
### Install and update all required libraries
pip install --upgrade pip
pip install --upgrade setuptools
pip install wheel
pip install cython
pip install -r requirements.txt
pip install simplejson
pip install pypiwin32
python setup.py build_ext --inplace
```

### For running
`python petfil.py`

### For packaging
Please find further informations about building a development environment and packaging in the script [release_windows.bat](release_windows.bat) where we implemented an automated build for windows.

