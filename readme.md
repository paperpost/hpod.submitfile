# hpod-submitfile

Submit a file to an H-POD instance via the API

Command-line Parameters

    -i   The DNS name of your H-POD instance
    -f   The path to the PDF to submit
    -o   The path to the JSON file containing the print options to use
    -l   A text file containing a list of files to include as attachments to the submission
    -a   The login for the account where the submission is to be made
    -u   The submitting user's email address
    -p   The submitting user's password

For example:

```
hpod-submitfile -i "test.h-pod.co.uk" -f "C:\file.pdf" -o "C:\options.json" -a myaccount -u test@test.com -p mypassword
```

Documentation on the H-POD API can be found at https://apidocs.h-pod.co.uk/