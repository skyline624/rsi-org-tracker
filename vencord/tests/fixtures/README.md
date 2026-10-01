# Certificat de test local

`tracker-test.crt` et `tracker-test.key` forment une paire auto-signée RSA 2048/SHA-256 créée
uniquement pour les serveurs HTTPS/TLS éphémères des tests. La clé privée est volontairement
publique : elle ne doit être utilisée pour aucun service ni certificat de production.

Le certificat désigne `127.0.0.1` (CN et SAN IP), expire en 2126 et ne provient d'aucun tracker
réel. Les tests lisent son empreinte depuis le certificat, sans valeur copiée à la main.

Pour créer une nouvelle paire de test avec OpenSSL :

```sh
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 36500 \
  -subj "/CN=127.0.0.1" -addext "subjectAltName=IP:127.0.0.1" \
  -keyout tracker-test.key -out tracker-test.crt
```

Depuis Git Bash sous Windows, préfixer la commande avec `MSYS_NO_PATHCONV=1` pour conserver
le sujet du certificat. Une paire équivalente peut aussi être créée avec l'API
`System.Security.Cryptography.X509Certificates.CertificateRequest` de .NET.
