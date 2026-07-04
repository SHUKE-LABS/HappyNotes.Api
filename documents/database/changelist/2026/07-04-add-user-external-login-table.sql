USE HappyNotes;
CREATE TABLE IF NOT EXISTS UserExternalLogin
(
    Id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    UserId          BIGINT       NOT NULL,
    Provider        VARCHAR(20)  NOT NULL,
    ProviderSubject VARCHAR(255) NOT NULL,
    CreatedAt       BIGINT       NOT NULL,
    UNIQUE KEY (Provider, ProviderSubject),
    INDEX (UserId)
);
